using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace JriksSoundboard.Core.Audio;

/// <param name="GameOutputDeviceId">Where sounds go so the game hears them — normally VB-Cable "CABLE Input". Required.</param>
/// <param name="MonitorDeviceId">Your headphones, so you hear the sounds too. Null = don't monitor.</param>
/// <param name="MicDeviceId">Your real microphone, mixed into the game output when passthrough is on.</param>
/// <param name="MicPassthrough">Open the real mic at all (the gate then decides when it's audible).</param>
public sealed record AudioEngineConfig(
    string GameOutputDeviceId,
    string? MonitorDeviceId,
    string? MicDeviceId,
    bool MicPassthrough);

/// <summary>A sound that is currently playing. Progress tracks the game output.</summary>
public sealed class SoundPlayback
{
    private readonly CachedSoundSampleProvider provider;

    internal SoundPlayback(CachedSoundSampleProvider provider) => this.provider = provider;

    public CachedSound Sound => provider.Sound;
    public TimeSpan Elapsed => EngineFormat.DurationOf(provider.Position);
    public TimeSpan Remaining => Sound.Duration - Elapsed;
    public double Progress => Sound.Samples.Length == 0 ? 1 : (double)provider.Position / Sound.Samples.Length;
    public bool IsFinished => provider.Position >= Sound.Samples.Length;
}

/// <summary>
/// Mixes soundboard sounds (+ optionally your real mic) into the virtual-mic device, and copies
/// the sounds to your headphones. Everything here is plain Windows audio (WASAPI shared mode):
/// the game only ever sees "a microphone" — nothing touches the game process.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int OutputLatencyMs = 40;
    private const int MicBufferMs = 20;
    private const string MmcssTask = "Audio";
    private static readonly TimeSpan MaxMicLag = TimeSpan.FromMilliseconds(120);

    private readonly object gate = new();
    private readonly List<IDisposable> devices = [];

    private WasapiPlayer? gameOut;
    private WasapiPlayer? monitorOut;
    private WasapiRecorder? micRecorder;
    private MixingSampleProvider? gameSounds;
    private MixingSampleProvider? monitorSounds;
    private MixingSampleProvider? previewSounds;
    private VolumeSampleProvider? gameSoundVolume;
    private VolumeSampleProvider? monitorVolume;
    private VolumeSampleProvider? micVolume;
    private MicGateSampleProvider? micGate;
    private SoundPlayback? latest;

    private float soundVolume = 1f;
    private float monitorVolumeLevel = 0.6f;
    private float micVolumeLevel = 1f;
    private MicMode micMode = MicMode.GamePushToTalk;
    private bool pushToTalkHeld;
    private float voiceThresholdDb = -40f;

    /// <summary>Raised when a device fails or disappears mid-session.</summary>
    public event EventHandler<string>? DeviceError;

    public bool IsRunning { get; private set; }

    public AudioEngineConfig? Config { get; private set; }

    /// <summary>The most recently started game sound, if any (for the "time left" display).</summary>
    public SoundPlayback? LatestPlayback => latest;

    /// <summary>Volume of soundboard sounds as heard in game. 1.0 = original.</summary>
    public float SoundVolume
    {
        get => soundVolume;
        set { soundVolume = value; if (gameSoundVolume is not null) gameSoundVolume.Volume = value; }
    }

    /// <summary>Volume of everything you hear in your headphones (copies + previews).</summary>
    public float MonitorVolume
    {
        get => monitorVolumeLevel;
        set { monitorVolumeLevel = value; if (monitorVolume is not null) monitorVolume.Volume = value; }
    }

    /// <summary>Volume of your real mic in the game output.</summary>
    public float MicVolume
    {
        get => micVolumeLevel;
        set { micVolumeLevel = value; if (micVolume is not null) micVolume.Volume = value; }
    }

    public MicMode MicMode
    {
        get => micMode;
        set { micMode = value; if (micGate is not null) micGate.Mode = value; }
    }

    /// <summary>Set by the input thread while the app's push-to-talk key is held.</summary>
    public bool MicPushToTalkHeld
    {
        get => pushToTalkHeld;
        set { pushToTalkHeld = value; if (micGate is not null) micGate.KeyHeld = value; }
    }

    public float VoiceThresholdDb
    {
        get => voiceThresholdDb;
        set { voiceThresholdDb = value; if (micGate is not null) micGate.Threshold = DbToLinear(value); }
    }

    /// <summary>Current raw mic level in dBFS (−100 when the mic is closed or silent).</summary>
    public float MicLevelDb
    {
        get
        {
            float level = micGate?.Level ?? 0f;
            return level <= 0.00001f ? -100f : 20f * MathF.Log10(level);
        }
    }

    public void Start(AudioEngineConfig config)
    {
        lock (gate)
        {
            StopCore();

            // WasapiPlayer/Recorder capture the current SynchronizationContext and raise their events
            // on it. Building them without the UI context keeps events on the audio threads, so a
            // blocking Dispose on the UI thread can never deadlock waiting for a UI-posted event.
            SynchronizationContext? uiContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                using var enumerator = new MMDeviceEnumerator();

                // Game output: sounds (+ gated mic) -> virtual cable.
                gameSounds = new MixingSampleProvider(EngineFormat.WaveFormat) { ReadFully = true };
                gameSoundVolume = new VolumeSampleProvider(gameSounds) { Volume = soundVolume };
                var gameMix = new MixingSampleProvider(EngineFormat.WaveFormat) { ReadFully = true };
                gameMix.AddMixerInput(gameSoundVolume);

                if (config.MicPassthrough && config.MicDeviceId is not null)
                {
                    gameMix.AddMixerInput(CreateMic(enumerator, config.MicDeviceId));
                }

                gameOut = CreateOutput(enumerator, config.GameOutputDeviceId, gameMix);

                // Monitor output: copies of game sounds + previews (never your own mic) -> headphones.
                if (config.MonitorDeviceId is not null)
                {
                    monitorSounds = new MixingSampleProvider(EngineFormat.WaveFormat) { ReadFully = true };
                    previewSounds = new MixingSampleProvider(EngineFormat.WaveFormat) { ReadFully = true };
                    var monitorMix = new MixingSampleProvider(EngineFormat.WaveFormat) { ReadFully = true };
                    monitorMix.AddMixerInput(monitorSounds);
                    monitorMix.AddMixerInput(previewSounds);
                    monitorVolume = new VolumeSampleProvider(monitorMix) { Volume = monitorVolumeLevel };
                    monitorOut = CreateOutput(enumerator, config.MonitorDeviceId, monitorVolume);
                }

                micRecorder?.StartRecording();
                gameOut.Play();
                monitorOut?.Play();

                Config = config;
                IsRunning = true;
            }
            catch
            {
                StopCore();
                throw;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(uiContext);
            }
        }
    }

    /// <summary>Plays a sound into the game (and your headphones). Thread-safe.</summary>
    public SoundPlayback Play(CachedSound sound, float gain = 1f, bool stopOthers = false)
    {
        lock (gate)
        {
            if (gameSounds is null)
            {
                throw new InvalidOperationException("Audio engine is not running.");
            }

            if (stopOthers)
            {
                gameSounds.RemoveAllMixerInputs();
                monitorSounds?.RemoveAllMixerInputs();
            }

            var gameProvider = new CachedSoundSampleProvider(sound);
            gameSounds.AddMixerInput(gameProvider.WithGain(gain));
            monitorSounds?.AddMixerInput(new CachedSoundSampleProvider(sound).WithGain(gain));

            var playback = new SoundPlayback(gameProvider);
            latest = playback;
            return playback;
        }
    }

    /// <summary>Plays a sound in your headphones only (the game never hears it). False if monitoring is off.</summary>
    public bool Preview(CachedSound sound, float gain = 1f)
    {
        lock (gate)
        {
            if (previewSounds is null)
            {
                return false;
            }
            previewSounds.RemoveAllMixerInputs();
            previewSounds.AddMixerInput(new CachedSoundSampleProvider(sound).WithGain(gain));
            return true;
        }
    }

    /// <summary>Stops a headphone-only preview (game sounds keep playing).</summary>
    public void StopPreview()
    {
        lock (gate)
        {
            previewSounds?.RemoveAllMixerInputs();
        }
    }

    public void StopAllSounds()
    {
        lock (gate)
        {
            gameSounds?.RemoveAllMixerInputs();
            monitorSounds?.RemoveAllMixerInputs();
            previewSounds?.RemoveAllMixerInputs();
            latest = null;
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            StopCore();
        }
    }

    public void Dispose() => Stop();

    private static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);

    private ISampleProvider CreateMic(MMDeviceEnumerator enumerator, string deviceId)
    {
        MMDevice device = enumerator.GetDevice(deviceId);
        devices.Add(device);

        // Ask Windows to deliver the mic already in our engine format (shared-mode auto-convert).
        micRecorder = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithBufferLength(MicBufferMs)
            .WithFormat(EngineFormat.WaveFormat)
            .WithMmcssThreadPriority(MmcssTask)
            .Build();

        WaveFormat format = micRecorder.WaveFormat.AsStandardWaveFormat();
        var buffer = new BufferedWaveProvider(format, TimeSpan.FromMilliseconds(500))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };

        micRecorder.DataAvailable += (data, flags, _, _) =>
        {
            // Mic and cable run on different clocks; if the mic runs ahead, drop the backlog
            // instead of letting your voice drift further and further behind.
            if (buffer.BufferedDuration > MaxMicLag)
            {
                buffer.ClearBuffer();
            }

            if ((flags & AudioClientBufferFlags.Silent) != 0)
            {
                buffer.AddSamples(new byte[data.Length]);
            }
            else
            {
                buffer.AddSamples(data);
            }
        };
        micRecorder.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                DeviceError?.Invoke(this, $"Microphone stopped: {e.Exception.Message}");
            }
        };

        micGate = new MicGateSampleProvider(buffer.ToSampleProvider().ToEngineFormat())
        {
            Mode = micMode,
            KeyHeld = pushToTalkHeld,
            Threshold = DbToLinear(voiceThresholdDb),
        };
        micVolume = new VolumeSampleProvider(micGate) { Volume = micVolumeLevel };
        return micVolume;
    }

    private WasapiPlayer CreateOutput(MMDeviceEnumerator enumerator, string deviceId, ISampleProvider source)
    {
        MMDevice device = enumerator.GetDevice(deviceId);
        devices.Add(device);
        string name = device.FriendlyName;

        WasapiPlayer player = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(OutputLatencyMs)
            .WithMmcssThreadPriority(MmcssTask)
            .Build();

        ISampleProvider chain = new ClampSampleProvider(source);
        int deviceRate = player.DeviceMixFormat.SampleRate;
        if (deviceRate != EngineFormat.SampleRate)
        {
            chain = new WdlResamplingSampleProvider(chain, deviceRate);
        }

        player.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                DeviceError?.Invoke(this, $"Output '{name}' stopped: {e.Exception.Message}");
            }
        };
        player.Init(chain.ToWaveProvider());
        return player;
    }

    private void StopCore()
    {
        IsRunning = false;
        latest = null;

        DisposeQuietly(ref gameOut);
        DisposeQuietly(ref monitorOut);
        DisposeQuietly(ref micRecorder);
        foreach (IDisposable device in devices)
        {
            device.Dispose();
        }
        devices.Clear();

        gameSounds = null;
        monitorSounds = null;
        previewSounds = null;
        gameSoundVolume = null;
        monitorVolume = null;
        micVolume = null;
        micGate = null;
    }

    private static void DisposeQuietly<T>(ref T? item) where T : class, IDisposable
    {
        try
        {
            item?.Dispose();
        }
        catch (Exception ex)
        {
            // A device that was unplugged can throw on dispose; nothing useful to do but note it.
            System.Diagnostics.Debug.WriteLine($"Dispose failed for {typeof(T).Name}: {ex.Message}");
        }
        item = null;
    }
}
