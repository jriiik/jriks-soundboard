using JriksSoundboard.Core.Audio;
using JriksSoundboard.Core.Board;
using JriksSoundboard.Core.Input;
using JriksSoundboard.Core.Settings;
using JriksSoundboard.Core.Wheel;

namespace JriksSoundboard.Core.Runtime;

/// <summary>
/// Wires everything together: passive hotkey listening → wheel → sound cache → audio engine.
/// The UI owns the editable <see cref="SoundBoard"/> and <see cref="AppSettings"/> and calls
/// <see cref="BoardChanged"/> / <see cref="ApplySettings"/> after edits (on the UI thread).
/// Events may be raised on background threads.
/// </summary>
public sealed class SoundboardRuntime : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(15);

    private readonly string settingsPath;
    private readonly string boardPath;
    private readonly Timer tickTimer;
    private readonly CachedSound cancelCue = CachedSound.CreateCancelCue();
    private TaskCompletionSource<int?>? capture;
    private CancellationTokenSource? preloadCts;
    private volatile BoardSnapshot snapshot = BoardSnapshot.Empty;
    private int backgroundInputs;
    private bool started;
    private bool disposed;

    public SoundboardRuntime(AppSettings settings, SoundBoard board, string settingsPath, string boardPath)
    {
        Settings = settings;
        Board = board;
        this.settingsPath = settingsPath;
        this.boardPath = boardPath;
        tickTimer = new Timer(_ => Wheels.Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Engine.DeviceError += (_, message) =>
        {
            SetAudioStatus(new AudioStatus(false, message + " — open Settings → Audio and press Restart audio."));
            RaiseLog("⚠ " + message);
        };
    }

    public AppSettings Settings { get; }

    public SoundBoard Board { get; }

    public AudioEngine Engine { get; } = new();

    public SoundCache Cache { get; } = new();

    public WheelController Wheels { get; } = new();

    public RawInputListener Input { get; } = new();

    /// <summary>False in UI snapshot mode so nothing is written to the real data folder.</summary>
    public bool PersistenceEnabled { get; init; } = true;

    public AudioStatus AudioStatus { get; private set; } = new(false, "Audio not started");

    public string? InputError { get; private set; }

    /// <summary>Display name of the last sound sent to the game (library names can differ from file names).</summary>
    public string? LastPlayedName { get; private set; }

    /// <summary>Key/button presses seen while another app (the game) had focus — proves background input works.</summary>
    public int BackgroundInputCount => Volatile.Read(ref backgroundInputs);

    /// <summary>Supplied by the UI: is one of our windows in the foreground? (Called on the input thread.)</summary>
    public Func<bool>? IsAppInForeground { get; set; }

    public event Action<string>? Log;

    public event Action<AudioStatus>? AudioStatusChanged;

    public event Action<WheelSnapshot>? WheelOpened;

    /// <summary>Wheel closed; the string is the sound name when one was chosen.</summary>
    public event Action<WheelSnapshot, WheelCloseReason, string?>? WheelClosed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
        {
            return;
        }
        started = true;

        Wheels.Opened += OnWheelOpened;
        Wheels.Closed += OnWheelClosed;
        Input.ButtonDown += OnButtonDown;
        Input.ButtonUp += OnButtonUp;
        Input.MouseMoved += Wheels.MouseMoved;
        Input.HandlerError += ex => RaiseLog($"Input handler error: {ex.Message}");

        ApplySettings();
        BoardChanged(save: false);
        RestartAudio();

        try
        {
            Input.Start();
            RaiseLog("Listening for hotkeys (passive Raw Input — nothing is sent to the game).");
        }
        catch (Exception ex)
        {
            InputError = ex.Message;
            RaiseLog("✗ Hotkey listener failed: " + ex.Message);
        }
    }

    // ───────────────────────── audio ─────────────────────────

    /// <summary>(Re)opens the audio devices from the current settings.</summary>
    public void RestartAudio()
    {
        IReadOnlyList<AudioDeviceInfo> outputs = AudioDevices.GetOutputDevices();
        IReadOnlyList<AudioDeviceInfo> inputs = AudioDevices.GetInputDevices();

        AudioDeviceInfo? game = Find(outputs, Settings.GameOutputDeviceId)
                                ?? outputs.FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
        if (game is null)
        {
            Engine.Stop();
            SetAudioStatus(new AudioStatus(false, "No virtual mic found — install VB-Cable, then pick it in Settings → Audio."));
            return;
        }
        Settings.GameOutputDeviceId ??= game.Id;

        AudioDeviceInfo? monitor = null;
        if (Settings.MonitorEnabled)
        {
            List<AudioDeviceInfo> candidates = outputs.Where(d => !d.IsVbCable).ToList();
            monitor = Find(candidates, Settings.MonitorDeviceId)
                      ?? Find(candidates, AudioDevices.GetDefaultOutput()?.Id)
                      ?? candidates.FirstOrDefault();
            Settings.MonitorDeviceId ??= monitor?.Id;
        }

        AudioDeviceInfo? mic = null;
        if (Settings.MicMode != MicMode.Off)
        {
            List<AudioDeviceInfo> mics = inputs.Where(d => !d.IsVbCable).ToList();
            mic = Find(mics, Settings.MicDeviceId)
                  ?? Find(mics, AudioDevices.GetDefaultInput()?.Id)
                  ?? mics.FirstOrDefault();
            Settings.MicDeviceId ??= mic?.Id;
        }

        ApplyLevels();
        try
        {
            Engine.Start(new AudioEngineConfig(game.Id, monitor?.Id, mic?.Id, mic is not null));
            bool ok = game.IsVbCable;
            string message = ok
                ? $"Ready — sounds go to {game.Name}"
                : $"Sounds go to {game.Name}, which isn't VB-Cable — the game won't hear them";
            SetAudioStatus(new AudioStatus(ok, message));
            RaiseLog($"Audio started: game → {game.Name}; headphones → {monitor?.Name ?? "off"}; mic → {mic?.Name ?? "off"}");
        }
        catch (Exception ex)
        {
            SetAudioStatus(new AudioStatus(false, "Audio error: " + ex.Message));
            RaiseLog("✗ Audio failed to start: " + ex.Message);
        }
    }

    /// <summary>Applies settings that don't need the devices reopened (restarts audio only if the mic must open/close).</summary>
    public void ApplySettings()
    {
        Wheels.Options = Settings.ToWheelOptions();
        ApplyLevels();
        Engine.MicMode = Settings.MicMode;
        Engine.VoiceThresholdDb = Settings.VoiceThresholdDb;
        if (Settings.MicMode != MicMode.AppPushToTalk)
        {
            Engine.MicPushToTalkHeld = false;
        }

        bool micNeeded = Settings.MicMode != MicMode.Off;
        bool micOpen = Engine.Config?.MicPassthrough ?? false;
        if (started && Engine.IsRunning && micNeeded != micOpen)
        {
            RestartAudio();
        }
    }

    private void ApplyLevels()
    {
        Engine.SoundVolume = Settings.SoundVolume;
        Engine.MonitorVolume = Settings.MonitorVolume;
        Engine.MicVolume = Settings.MicVolume;
    }

    private void SetAudioStatus(AudioStatus status)
    {
        AudioStatus = status;
        AudioStatusChanged?.Invoke(status);
    }

    private static AudioDeviceInfo? Find(IEnumerable<AudioDeviceInfo> devices, string? id) =>
        id is null ? null : devices.FirstOrDefault(d => d.Id == id);

    // ───────────────────────── board ─────────────────────────

    /// <summary>Call on the UI thread after any library/wheel edit: refreshes the hotkey snapshot, saves, preloads.</summary>
    public void BoardChanged(bool save = true)
    {
        Board.RefreshUsage();
        BoardSnapshot fresh = BoardSnapshot.From(Board);
        snapshot = fresh;
        Wheels.Board = fresh;
        if (save)
        {
            SaveBoard();
        }
        PreloadWheelSounds(fresh);
    }

    public bool SaveBoard() => TrySave(() => BoardStore.Save(Board, boardPath), "library");

    public bool SaveSettings() => TrySave(() => SettingsStore.Save(Settings, settingsPath), "settings");

    private bool TrySave(Action save, string what)
    {
        if (!PersistenceEnabled)
        {
            return true;
        }
        try
        {
            save();
            return true;
        }
        catch (Exception ex)
        {
            RaiseLog($"✗ Couldn't save {what}: {ex.Message}");
            return false;
        }
    }

    private void PreloadWheelSounds(BoardSnapshot board)
    {
        preloadCts?.Cancel();
        var cts = new CancellationTokenSource();
        preloadCts = cts;

        List<string> paths = board.Wheels
            .SelectMany(w => BoardSnapshot.SoundIdsIn(w.Root))
            .Distinct()
            .Select(id => board.Sounds.TryGetValue(id, out SoundSnapshot? s) ? s.FilePath : null)
            .OfType<string>()
            .ToList();

        Task.Run(() =>
        {
            foreach ((string path, Exception error) in Cache.Preload(paths, cts.Token))
            {
                RaiseLog($"⚠ Couldn't load {Path.GetFileName(path)}: {error.Message}");
            }
        });
    }

    // ───────────────────────── playback ─────────────────────────

    /// <summary>Plays a library sound into the game (and your headphones).</summary>
    public void PlaySound(SoundItem sound) => PlayInBackground(sound.Name, sound.FilePath, sound.Volume, preview: false);

    /// <summary>Plays a library sound in your headphones only.</summary>
    public void PreviewSound(SoundItem sound) => PlayInBackground(sound.Name, sound.FilePath, sound.Volume, preview: true);

    public void PlayTestTone() => Engine.Play(CachedSound.CreateTestTone(), 1f, stopOthers: true);

    public void StopAll() => Engine.StopAllSounds();

    private void PlayInBackground(string name, string path, float volume, bool preview)
    {
        Task.Run(() =>
        {
            try
            {
                CachedSound sound = Cache.Get(path);
                float gain = volume * (Settings.NormalizeLoudness ? sound.LoudnessGain() : 1f);
                if (preview)
                {
                    if (!Engine.Preview(sound, gain))
                    {
                        RaiseLog("Turn on “Hear sounds yourself” in Settings → Audio to preview sounds.");
                    }
                }
                else if (Engine.IsRunning)
                {
                    LastPlayedName = name;
                    Engine.Play(sound, gain, Settings.StopPreviousOnPlay);
                    RaiseLog($"▶ {name}");
                }
                else
                {
                    RaiseLog($"Can't play “{name}”: audio isn't running ({AudioStatus.Message}).");
                }
            }
            catch (FileNotFoundException)
            {
                RaiseLog($"✗ “{name}”: the file is missing ({path}).");
            }
            catch (Exception ex)
            {
                RaiseLog($"✗ Couldn't play “{name}”: {ex.Message}");
            }
        });
    }

    // ───────────────────────── hotkeys ─────────────────────────

    /// <summary>Waits for the next key or mouse button (Esc = cancel → null). Left mouse is ignored so you can click Cancel.</summary>
    public Task<int?> CaptureNextKeyAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Wheels.Cancel();
        Volatile.Write(ref capture, tcs);
        cancellationToken.Register(() =>
        {
            if (Interlocked.CompareExchange(ref capture, null, tcs) == tcs)
            {
                tcs.TrySetResult(null);
            }
        });
        return tcs.Task;
    }

    /// <summary>Describes why a key can't be used (null = free). Call on the UI thread.</summary>
    public string? FindHotkeyConflict(int virtualKey, Guid? exceptWheel = null, bool exceptStopKey = false, bool exceptPushToTalk = false)
    {
        if (virtualKey == VirtualKeys.None)
        {
            return null;
        }
        if (virtualKey == VirtualKeys.MouseLeft)
        {
            return "Left mouse can't be a hotkey — it would trigger every time you shoot.";
        }
        foreach (WheelDefinition wheel in Board.Wheels)
        {
            if (wheel.Hotkey == virtualKey && wheel.Id != exceptWheel)
            {
                return $"{VirtualKeys.GetName(virtualKey)} already opens “{wheel.Name}”.";
            }
        }
        if (!exceptStopKey && Settings.StopKey == virtualKey)
        {
            return $"{VirtualKeys.GetName(virtualKey)} is already the stop-all key.";
        }
        if (!exceptPushToTalk && Settings.MicMode == MicMode.AppPushToTalk && Settings.PushToTalkKey == virtualKey)
        {
            return $"{VirtualKeys.GetName(virtualKey)} is already your push-to-talk key.";
        }
        return null;
    }

    // Runs on the raw-input thread.
    private void OnButtonDown(int vk)
    {
        if (IsAppInForeground is { } inForeground && !inForeground())
        {
            Interlocked.Increment(ref backgroundInputs);
        }

        TaskCompletionSource<int?>? pending = Volatile.Read(ref capture);
        if (pending is not null)
        {
            if (vk != VirtualKeys.MouseLeft && Interlocked.CompareExchange(ref capture, null, pending) == pending)
            {
                pending.TrySetResult(vk == VirtualKeys.Escape ? null : vk);
            }
            return;
        }

        if (vk == Settings.StopKey && vk != VirtualKeys.None)
        {
            StopAll();
            RaiseLog("■ Stop-all key");
            return;
        }
        if (Settings.MicMode == MicMode.AppPushToTalk && vk == Settings.PushToTalkKey && vk != VirtualKeys.None)
        {
            Engine.MicPushToTalkHeld = true;
        }
        Wheels.ButtonDown(vk);
    }

    // Runs on the raw-input thread.
    private void OnButtonUp(int vk)
    {
        if (vk == Settings.PushToTalkKey && vk != VirtualKeys.None)
        {
            Engine.MicPushToTalkHeld = false;
        }
        Wheels.ButtonUp(vk);
    }

    private void OnWheelOpened(WheelSnapshot wheel)
    {
        tickTimer.Change(TimeSpan.Zero, TickInterval);
        WheelOpened?.Invoke(wheel);
    }

    // Runs on the input thread (release) or the tick timer (timeout).
    private void OnWheelClosed(WheelSnapshot wheel, WheelCloseReason reason, SlotSnapshot? slot)
    {
        tickTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        string? soundName = null;
        switch (reason)
        {
            case WheelCloseReason.SoundChosen when slot?.SoundId is Guid id && snapshot.Sounds.TryGetValue(id, out SoundSnapshot? sound):
                soundName = sound.Name;
                PlayInBackground(sound.Name, sound.FilePath, sound.Volume, preview: false);
                break;
            case WheelCloseReason.TimedOut:
                RaiseLog("Wheel auto-cancelled (held too long)");
                if (Settings.TimeoutCue)
                {
                    Engine.Preview(cancelCue);
                }
                break;
            case WheelCloseReason.ReleasedInCentre:
                RaiseLog("Wheel cancelled (released in the centre)");
                break;
        }

        WheelClosed?.Invoke(wheel, reason, soundName);
    }

    private void RaiseLog(string message) => Log?.Invoke(message);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        preloadCts?.Cancel();
        tickTimer.Dispose();
        Input.Dispose();
        Engine.Dispose();
        SaveSettings();
    }
}
