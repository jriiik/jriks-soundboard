using NAudio.Vorbis;
using NAudio.Wave;

namespace JriksSoundboard.Core.Audio;

/// <summary>
/// A sound fully decoded into memory in <see cref="EngineFormat"/>, so playback starts
/// instantly with no disk access or decoding at the moment you release the wheel key.
/// </summary>
public sealed class CachedSound
{
    /// <summary>Longer files are cut off here to keep memory use sane (~110 MB at this cap).</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(5);

    public static readonly IReadOnlySet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".ogg", ".oga", ".flac", ".m4a", ".aac", ".wma", ".aif", ".aiff",
    };

    private CachedSound(string name, float[] samples, bool wasTruncated)
    {
        Name = name;
        Samples = samples;
        WasTruncated = wasTruncated;
        (Peak, Rms) = Analyze(samples);
    }

    public string Name { get; }

    /// <summary>Interleaved stereo float samples at 48 kHz.</summary>
    public float[] Samples { get; }

    public bool WasTruncated { get; }

    public TimeSpan Duration => EngineFormat.DurationOf(Samples.Length);

    /// <summary>Highest absolute sample value (0–1+).</summary>
    public float Peak { get; }

    /// <summary>RMS level of the non-silent parts (silence padding doesn't drag it down).</summary>
    public float Rms { get; }

    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Gain that brings this sound to a common loudness so a quiet clip and an ear-rape clip end
    /// up similar. Boosts are limited so the sound never clips, and to at most <paramref name="maxGain"/>.
    /// </summary>
    public float LoudnessGain(float targetRmsDb = -16f, float maxGain = 4f)
    {
        if (Rms <= 0f || Peak <= 0f)
        {
            return 1f;
        }
        float target = MathF.Pow(10f, targetRmsDb / 20f);
        float gain = Math.Min(target / Rms, maxGain);
        return Math.Min(gain, 0.98f / Peak);
    }

    /// <summary>Decodes wav, mp3, m4a/aac, wma, flac (via Windows Media Foundation) and ogg.</summary>
    public static CachedSound Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Sound file not found.", path);
        }

        using WaveStream reader = OpenReader(path);
        ISampleProvider source = reader as ISampleProvider ?? reader.ToSampleProvider();
        ISampleProvider converted = source.ToEngineFormat();

        int maxSamples = (int)(MaxDuration.TotalSeconds * EngineFormat.SampleRate * EngineFormat.Channels);
        var samples = new List<float>(EngineFormat.SampleRate * EngineFormat.Channels * 4);
        var buffer = new float[EngineFormat.SampleRate * EngineFormat.Channels / 10]; // 100 ms chunks
        bool truncated = false;

        int read;
        while ((read = converted.Read(buffer)) > 0)
        {
            int room = maxSamples - samples.Count;
            if (read >= room)
            {
                samples.AddRange(buffer.AsSpan(0, room));
                truncated = read > room || converted.Read(buffer) > 0;
                break;
            }
            samples.AddRange(buffer.AsSpan(0, read));
        }

        return new CachedSound(Path.GetFileNameWithoutExtension(path), samples.ToArray(), truncated);
    }

    /// <summary>Reads just the length of a file without decoding it. Returns false if it can't be opened.</summary>
    public static bool TryProbe(string path, out TimeSpan duration, out string? error)
    {
        try
        {
            using WaveStream reader = OpenReader(path);
            duration = reader.TotalTime;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            duration = TimeSpan.Zero;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Wraps already-generated samples (must be interleaved stereo, 48 kHz).</summary>
    public static CachedSound FromSamples(string name, float[] interleavedStereo)
    {
        if (interleavedStereo.Length % EngineFormat.Channels != 0)
        {
            throw new ArgumentException("Sample count must be a multiple of the channel count.", nameof(interleavedStereo));
        }
        return new CachedSound(name, interleavedStereo, wasTruncated: false);
    }

    /// <summary>A short two-note beep, handy for testing routing without picking a file.</summary>
    public static CachedSound CreateTestTone(double seconds = 1.0, float amplitude = 0.4f) =>
        new("Test tone", Tones([(660, seconds / 2), (880, seconds / 2)], amplitude, gapSeconds: 0), wasTruncated: false);

    /// <summary>A quiet descending "nope" blip — played only in your headphones when the wheel times out.</summary>
    public static CachedSound CreateCancelCue() =>
        new("Cancelled", Tones([(660, 0.07), (440, 0.09)], amplitude: 0.18f, gapSeconds: 0.03), wasTruncated: false);

    private static float[] Tones((double Frequency, double Seconds)[] notes, float amplitude, double gapSeconds)
    {
        int rate = EngineFormat.SampleRate;
        int gap = (int)(gapSeconds * rate);
        int totalFrames = notes.Sum(n => (int)(n.Seconds * rate)) + gap * Math.Max(0, notes.Length - 1);
        var samples = new float[totalFrames * EngineFormat.Channels];
        int fade = rate / 200; // 5 ms fade in/out per note to avoid clicks

        int frame = 0;
        for (int n = 0; n < notes.Length; n++)
        {
            int frames = (int)(notes[n].Seconds * rate);
            for (int i = 0; i < frames; i++, frame++)
            {
                float envelope = Math.Min(1f, Math.Min(i, frames - 1 - i) / (float)fade);
                float v = (float)Math.Sin(2 * Math.PI * notes[n].Frequency * i / rate) * amplitude * envelope;
                samples[frame * 2] = v;
                samples[frame * 2 + 1] = v;
            }
            if (n < notes.Length - 1)
            {
                frame += gap;
            }
        }
        return samples;
    }

    private static (float Peak, float Rms) Analyze(float[] samples)
    {
        const double silencePower = 1e-5; // -50 dBFS: quieter 10 ms blocks are treated as silence
        int block = EngineFormat.SampleRate / 100 * EngineFormat.Channels;
        float peak = 0f;
        double loudPower = 0;
        long loudCount = 0;

        for (int start = 0; start < samples.Length; start += block)
        {
            int end = Math.Min(samples.Length, start + block);
            double sum = 0;
            for (int i = start; i < end; i++)
            {
                float v = samples[i];
                float abs = Math.Abs(v);
                if (abs > peak)
                {
                    peak = abs;
                }
                sum += v * v;
            }
            if (sum / (end - start) >= silencePower)
            {
                loudPower += sum;
                loudCount += end - start;
            }
        }

        float rms = loudCount == 0 ? 0f : (float)Math.Sqrt(loudPower / loudCount);
        return (peak, rms);
    }

    private static WaveStream OpenReader(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".ogg" or ".oga"
            ? new VorbisWaveReader(path)
            : new AudioFileReader(path);
    }
}
