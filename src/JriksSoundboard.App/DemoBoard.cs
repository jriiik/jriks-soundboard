using System.IO;
using JriksSoundboard.Core.Audio;
using JriksSoundboard.Core.Board;
using JriksSoundboard.Core.Input;
using NAudio.Wave;

namespace JriksSoundboard.App;

/// <summary>Generates a small sample library (tone WAVs) + two wheels for UI snapshot mode.</summary>
internal static class DemoBoard
{
    public static SoundBoard Create(string folder)
    {
        Directory.CreateDirectory(folder);
        var board = new SoundBoard();

        SoundItem Add(string name, double seconds, double frequency)
        {
            string path = Path.Combine(folder, name + ".wav");
            float[] samples = Tone(seconds, frequency);
            using (var writer = new WaveFileWriter(path, EngineFormat.WaveFormat))
            {
                writer.WriteSamples(samples, 0, samples.Length);
            }
            var item = new SoundItem { Name = name, FilePath = path, DurationSeconds = seconds };
            board.Sounds.Add(item);
            return item;
        }

        SoundItem niceShot = Add("Nice shot", 1.2, 520);
        SoundItem letsGo = Add("Let's gooo", 2.0, 600);
        SoundItem airhorn = Add("Airhorn", 1.5, 880);
        SoundItem bruh = Add("Bruh", 0.8, 180);
        SoundItem violin = Add("Sad violin", 4.5, 440);
        SoundItem oof = Add("Oof", 0.6, 220);
        SoundItem wow = Add("Wow", 1.1, 700);
        SoundItem clap = Add("Slow clap", 3.4, 1200);
        SoundItem rushB = Add("Rush B", 1.6, 330);
        Add("Emotional damage", 2.2, 400);

        var memes = new WheelDefinition { Name = "Memes", Hotkey = VirtualKeys.Mouse4 };
        memes.Root.Slots[0].AssignSound(niceShot.Id);
        memes.Root.Slots[1].AssignSound(letsGo.Id);
        memes.Root.Slots[2].AssignSound(airhorn.Id);
        memes.Root.Slots[3].MakeFolder("Reactions");
        memes.Root.Slots[3].Folder!.Slots[0].AssignSound(wow.Id);
        memes.Root.Slots[3].Folder!.Slots[2].AssignSound(oof.Id);
        memes.Root.Slots[3].Folder!.Slots[4].AssignSound(clap.Id);
        memes.Root.Slots[5].AssignSound(violin.Id);
        memes.Root.Slots[6].AssignSound(bruh.Id);

        var callouts = new WheelDefinition { Name = "Callouts", Hotkey = VirtualKeys.Mouse5, Root = WheelPage.Create(6) };
        callouts.Root.Slots[0].AssignSound(rushB.Id);

        board.Wheels.Add(memes);
        board.Wheels.Add(callouts);
        board.Normalize();
        return board;
    }

    private static float[] Tone(double seconds, double frequency)
    {
        int frames = (int)(seconds * EngineFormat.SampleRate);
        var samples = new float[frames * EngineFormat.Channels];
        int fade = EngineFormat.SampleRate / 100;
        for (int i = 0; i < frames; i++)
        {
            float envelope = Math.Min(1f, Math.Min(i, frames - 1 - i) / (float)fade);
            float v = (float)Math.Sin(2 * Math.PI * frequency * i / EngineFormat.SampleRate) * 0.3f * envelope;
            samples[i * 2] = v;
            samples[i * 2 + 1] = v;
        }
        return samples;
    }
}
