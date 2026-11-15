using NAudio.Wave;

namespace JriksSoundboard.Core.Audio;

/// <summary>
/// The single internal audio format everything is converted to before mixing:
/// 32-bit float, 48 kHz, stereo. Windows' shared-mode engine converts this to
/// whatever each output device actually runs at.
/// </summary>
public static class EngineFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    public static WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    public static TimeSpan DurationOf(int interleavedSampleCount) =>
        TimeSpan.FromSeconds((double)interleavedSampleCount / (SampleRate * Channels));
}
