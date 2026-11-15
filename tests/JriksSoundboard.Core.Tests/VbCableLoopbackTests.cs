using System.Runtime.InteropServices;
using JriksSoundboard.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xunit.Abstractions;

namespace JriksSoundboard.Core.Tests;

/// <summary>
/// End-to-end check on real hardware: play a tone through the engine into "CABLE Input" and
/// record "CABLE Output" (the virtual mic the game would use). If the tone shows up there,
/// CS2 will receive it too. Returns early (and says so) if VB-Cable isn't installed.
/// </summary>
[Trait("Category", "Hardware")]
public class VbCableLoopbackTests(ITestOutputHelper output)
{
    [Fact]
    public void Tone_played_by_engine_arrives_on_the_virtual_mic()
    {
        AudioDeviceInfo? cableIn = AudioDevices.FindVbCableInput();
        AudioDeviceInfo? cableOut = AudioDevices.FindVbCableOutput();
        if (cableIn is null || cableOut is null)
        {
            output.WriteLine("SKIPPED: VB-Cable is not installed on this machine.");
            return;
        }

        SynchronizationContext.SetSynchronizationContext(null);

        using var engine = new AudioEngine();
        engine.Start(new AudioEngineConfig(cableIn.Id, MonitorDeviceId: null, MicDeviceId: null, MicPassthrough: false));

        using var enumerator = new MMDeviceEnumerator();
        using MMDevice captureDevice = enumerator.GetDevice(cableOut.Id);
        using WasapiRecorder recorder = new WasapiRecorderBuilder()
            .WithDevice(captureDevice)
            .WithFormat(EngineFormat.WaveFormat)
            .Build();

        WaveFormat captured = recorder.WaveFormat.AsStandardWaveFormat();
        Assert.Equal(WaveFormatEncoding.IeeeFloat, captured.Encoding);

        var gate = new object();
        float peak = 0;
        recorder.DataAvailable += (data, flags, _, _) =>
        {
            if ((flags & AudioClientBufferFlags.Silent) != 0)
            {
                return;
            }
            float local = 0;
            foreach (float sample in MemoryMarshal.Cast<byte, float>(data))
            {
                local = Math.Max(local, Math.Abs(sample));
            }
            lock (gate)
            {
                peak = Math.Max(peak, local);
            }
        };

        recorder.StartRecording();
        Thread.Sleep(400);
        float baseline;
        lock (gate)
        {
            baseline = peak;
            peak = 0;
        }

        engine.Play(CachedSound.CreateTestTone(seconds: 0.8, amplitude: 0.5f), stopOthers: true);
        Thread.Sleep(1300);
        recorder.StopRecording();

        float duringTone;
        lock (gate)
        {
            duringTone = peak;
        }

        output.WriteLine($"Virtual mic peak before tone: {baseline:0.000}, during tone: {duringTone:0.000} (tone amplitude 0.5)");
        Assert.True(duringTone > 0.1f, $"The tone did not reach CABLE Output (peak {duringTone:0.000}). Is CABLE Input muted in Windows sound settings?");
    }
}
