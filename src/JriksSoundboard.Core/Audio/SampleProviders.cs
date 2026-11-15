using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace JriksSoundboard.Core.Audio;

/// <summary>Plays a <see cref="CachedSound"/> from memory. Reports its own progress.</summary>
internal sealed class CachedSoundSampleProvider(CachedSound sound) : ISampleProvider
{
    private int position;

    public CachedSound Sound => sound;
    public WaveFormat WaveFormat => EngineFormat.WaveFormat;
    public int Position => Volatile.Read(ref position);

    public int Read(Span<float> buffer)
    {
        int pos = position;
        int remaining = sound.Samples.Length - pos;
        if (remaining <= 0)
        {
            return 0;
        }

        int count = Math.Min(remaining, buffer.Length);
        sound.Samples.AsSpan(pos, count).CopyTo(buffer);
        Volatile.Write(ref position, pos + count);
        return count;
    }
}

/// <summary>Hard-limits samples to [-1, 1] so layered sounds can't wrap/distort in the driver.</summary>
internal sealed class ClampSampleProvider(ISampleProvider source) : ISampleProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        int read = source.Read(buffer);
        for (int i = 0; i < read; i++)
        {
            buffer[i] = Math.Clamp(buffer[i], -1f, 1f);
        }
        return read;
    }
}

/// <summary>Folds a 3+ channel source down to stereo (front L/R plus centre).</summary>
internal sealed class DownmixToStereoSampleProvider : ISampleProvider
{
    private const float CentreGain = 0.7071f;
    private readonly ISampleProvider source;
    private readonly int sourceChannels;
    private float[] sourceBuffer = [];

    public DownmixToStereoSampleProvider(ISampleProvider source)
    {
        if (source.WaveFormat.Channels < 3)
        {
            throw new ArgumentException("Source must have 3 or more channels.", nameof(source));
        }
        this.source = source;
        sourceChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        int frames = buffer.Length / 2;
        int needed = frames * sourceChannels;
        if (sourceBuffer.Length < needed)
        {
            sourceBuffer = new float[needed];
        }

        int read = source.Read(sourceBuffer.AsSpan(0, needed));
        int framesRead = read / sourceChannels;
        for (int f = 0; f < framesRead; f++)
        {
            int s = f * sourceChannels;
            float centre = sourceBuffer[s + 2] * CentreGain;
            buffer[f * 2] = sourceBuffer[s] + centre;
            buffer[f * 2 + 1] = sourceBuffer[s + 1] + centre;
        }
        return framesRead * 2;
    }
}

/// <summary>
/// Opens/closes your real mic depending on <see cref="MicMode"/>, with a short fade so it
/// never clicks. Also measures the raw mic level for the settings meter.
/// </summary>
internal sealed class MicGateSampleProvider(ISampleProvider source) : ISampleProvider
{
    private static readonly int HoldSamples = EngineFormat.SampleRate * EngineFormat.Channels * 400 / 1000; // 400 ms
    private static readonly float FadeStep = 1f / (EngineFormat.SampleRate * EngineFormat.Channels * 0.008f); // 8 ms ramp

    private float gain;
    private int holdRemaining;
    private float level;
    private int mode;
    private int keyHeld;
    private float threshold = 0.01f;

    public WaveFormat WaveFormat => source.WaveFormat;

    public MicMode Mode
    {
        get => (MicMode)Volatile.Read(ref mode);
        set => Volatile.Write(ref mode, (int)value);
    }

    public bool KeyHeld
    {
        get => Volatile.Read(ref keyHeld) != 0;
        set => Volatile.Write(ref keyHeld, value ? 1 : 0);
    }

    /// <summary>Linear peak level (0–1) that opens the mic in voice-activation mode.</summary>
    public float Threshold
    {
        get => Volatile.Read(ref threshold);
        set => Volatile.Write(ref threshold, value);
    }

    /// <summary>Raw mic peak of the most recent buffer (0–1), before the gate and mic volume.</summary>
    public float Level => Volatile.Read(ref level);

    public int Read(Span<float> buffer)
    {
        int read = source.Read(buffer);

        float peak = 0f;
        for (int i = 0; i < read; i++)
        {
            peak = Math.Max(peak, Math.Abs(buffer[i]));
        }
        Volatile.Write(ref level, peak);

        bool open;
        switch (Mode)
        {
            case MicMode.GamePushToTalk:
                open = true;
                break;
            case MicMode.AppPushToTalk:
                open = KeyHeld;
                break;
            case MicMode.VoiceActivation:
                holdRemaining = peak >= Threshold ? HoldSamples : Math.Max(0, holdRemaining - read);
                open = holdRemaining > 0;
                break;
            default:
                open = false;
                break;
        }

        float target = open ? 1f : 0f;
        for (int i = 0; i < read; i++)
        {
            if (gain < target)
            {
                gain = Math.Min(target, gain + FadeStep);
            }
            else if (gain > target)
            {
                gain = Math.Max(target, gain - FadeStep);
            }
            buffer[i] *= gain;
        }
        return read;
    }
}

internal static class SampleProviderExtensions
{
    /// <summary>Wraps a provider in a volume stage unless the gain is effectively 1.</summary>
    public static ISampleProvider WithGain(this ISampleProvider source, float gain) =>
        Math.Abs(gain - 1f) < 0.001f ? source : new VolumeSampleProvider(source) { Volume = gain };

    /// <summary>Converts any float sample provider to <see cref="EngineFormat"/> (48 kHz stereo).</summary>
    public static ISampleProvider ToEngineFormat(this ISampleProvider source)
    {
        ISampleProvider p = source;
        if (p.WaveFormat.Channels > 2)
        {
            p = new DownmixToStereoSampleProvider(p);
        }
        if (p.WaveFormat.SampleRate != EngineFormat.SampleRate)
        {
            p = new WdlResamplingSampleProvider(p, EngineFormat.SampleRate);
        }
        if (p.WaveFormat.Channels == 1)
        {
            p = new MonoToStereoSampleProvider(p);
        }
        return p;
    }
}
