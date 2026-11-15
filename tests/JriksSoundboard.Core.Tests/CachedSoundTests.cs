using JriksSoundboard.Core.Audio;
using NAudio.Wave;

namespace JriksSoundboard.Core.Tests;

public sealed class CachedSoundTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "jriks-tests-" + Guid.NewGuid().ToString("N"));

    public CachedSoundTests() => Directory.CreateDirectory(tempDir);

    public void Dispose() => Directory.Delete(tempDir, recursive: true);

    [Fact]
    public void Mono_44k_16bit_wav_is_converted_to_48k_stereo()
    {
        string path = WriteSineWav("mono.wav", new WaveFormat(44_100, 16, 1), seconds: 0.5, frequency: 440);

        CachedSound sound = CachedSound.Load(path);

        Assert.Equal("mono", sound.Name);
        Assert.InRange(sound.Duration.TotalSeconds, 0.48, 0.52);
        Assert.Equal(0, sound.Samples.Length % 2);
        for (int i = 0; i < sound.Samples.Length; i += 2)
        {
            Assert.Equal(sound.Samples[i], sound.Samples[i + 1]); // mono duplicated to both ears
        }
        Assert.True(sound.Samples.Max(Math.Abs) > 0.3f, "Expected an audible signal after conversion.");
    }

    [Fact]
    public void Stereo_float_48k_wav_keeps_exact_length()
    {
        string path = WriteSineWav("stereo.wav", WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2), seconds: 1.0, frequency: 1000);

        CachedSound sound = CachedSound.Load(path);

        Assert.Equal(48_000 * 2, sound.Samples.Length);
        Assert.False(sound.WasTruncated);
    }

    [Fact]
    public void Missing_file_throws_FileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => CachedSound.Load(Path.Combine(tempDir, "nope.wav")));
    }

    [Fact]
    public void Test_tone_is_stereo_engine_format_and_fades_in()
    {
        CachedSound tone = CachedSound.CreateTestTone(seconds: 0.5);

        Assert.Equal(EngineFormat.SampleRate * EngineFormat.Channels / 2, tone.Samples.Length);
        Assert.Equal(0f, tone.Samples[0]); // starts silent (no click)
        Assert.True(tone.Samples.Max(Math.Abs) > 0.3f);
    }

    [Fact]
    public void FromSamples_rejects_partial_stereo_frames()
    {
        Assert.Throws<ArgumentException>(() => CachedSound.FromSamples("bad", new float[3]));
    }

    private string WriteSineWav(string name, WaveFormat format, double seconds, double frequency)
    {
        string path = Path.Combine(tempDir, name);
        int frames = (int)(format.SampleRate * seconds);
        using var writer = new WaveFileWriter(path, format);
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * i / format.SampleRate));
            for (int c = 0; c < format.Channels; c++)
            {
                writer.WriteSample(v);
            }
        }
        return path;
    }
}
