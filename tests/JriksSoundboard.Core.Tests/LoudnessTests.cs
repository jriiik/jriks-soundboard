using JriksSoundboard.Core.Audio;

namespace JriksSoundboard.Core.Tests;

public class LoudnessTests
{
    [Fact]
    public void Quiet_sound_is_boosted_without_clipping()
    {
        CachedSound quiet = Sine(amplitude: 0.05f, seconds: 1);

        float gain = quiet.LoudnessGain();

        Assert.True(gain > 1f);
        Assert.True(gain * quiet.Peak <= 0.98f + 1e-4f);
    }

    [Fact]
    public void Loud_sound_is_turned_down()
    {
        CachedSound loud = Sine(amplitude: 0.9f, seconds: 1);

        Assert.InRange(loud.LoudnessGain(), 0.2f, 0.3f); // ≈ -3.9 dBFS RMS brought to -16 dBFS
    }

    [Fact]
    public void Silence_is_left_alone()
    {
        CachedSound silence = CachedSound.FromSamples("silence", new float[48_000 * 2]);

        Assert.Equal(1f, silence.LoudnessGain());
    }

    [Fact]
    public void Silent_padding_does_not_make_a_sound_look_quieter()
    {
        CachedSound tone = Sine(amplitude: 0.5f, seconds: 0.5);
        float[] padded = new float[tone.Samples.Length + 48_000 * 2 * 2]; // + 2 s of silence
        tone.Samples.CopyTo(padded, 0);

        CachedSound withSilence = CachedSound.FromSamples("padded", padded);

        Assert.Equal(tone.Rms, withSilence.Rms, precision: 3);
    }

    private static CachedSound Sine(float amplitude, double seconds)
    {
        int frames = (int)(48_000 * seconds);
        var samples = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float v = amplitude * (float)Math.Sin(2 * Math.PI * 440 * i / 48_000);
            samples[i * 2] = v;
            samples[i * 2 + 1] = v;
        }
        return CachedSound.FromSamples("sine", samples);
    }
}
