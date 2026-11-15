using System.IO.Compression;
using System.Text.Json;
using JriksSoundboard.Core.Audio;
using JriksSoundboard.Core.Board;
using JriksSoundboard.Core.Input;
using JriksSoundboard.Core.Settings;
using NAudio.Wave;

namespace JriksSoundboard.Core.Tests;

public sealed class ConfigBackupTests : IDisposable
{
    private const string SoundName = "whatdoyoumean_1_fast_bored";
    private readonly string temp = Path.Combine(Path.GetTempPath(), "jriks-backup-" + Guid.NewGuid().ToString("N"));

    public ConfigBackupTests() => Directory.CreateDirectory(temp);

    public void Dispose() => Directory.Delete(temp, recursive: true);

    private string RestoreFolder => Path.Combine(temp, "restored");

    [Fact]
    public void Round_trip_restores_everything_and_the_audio_when_the_original_is_gone()
    {
        string wav = WriteWav("original.wav");
        (AppSettings settings, SoundBoard board, SoundItem sound) = Setup(wav);
        string zip = Path.Combine(temp, "backup.zip");

        BackupManifest saved = ConfigBackup.Save(zip, settings, board, includeAudio: true, appVersion: "test");
        File.Delete(wav);
        LoadedBackup loaded = ConfigBackup.Load(zip, RestoreFolder);

        Assert.Equal(1, saved.AudioFiles);
        Assert.Equal(1, loaded.SoundsRestoredFromBackup);
        Assert.Empty(loaded.MissingSounds);

        SoundItem restored = Assert.Single(loaded.Board.Sounds);
        Assert.Equal(sound.Id, restored.Id);
        Assert.Equal(0.7f, restored.Volume);
        Assert.True(File.Exists(restored.FilePath));
        Assert.Equal(Path.Combine(RestoreFolder, SoundName + ".wav"), restored.FilePath);

        WheelDefinition wheel = Assert.Single(loaded.Board.Wheels);
        Assert.Equal(VirtualKeys.Mouse4, wheel.Hotkey);
        Assert.Equal(16, wheel.Root.SliceCount);
        Assert.Equal("Sent 1", wheel.Root.Slots[3].Label);
        Assert.Equal(sound.Id, wheel.Root.Slots[3].Folder!.Slots[0].SoundId);

        Assert.Equal(75, loaded.Settings.Deadzone);
        Assert.Equal(2.5, loaded.Settings.AutoCancelSeconds);
        Assert.Equal(VirtualKeys.Mouse5, loaded.Settings.CancelKey);
        Assert.Equal(OverlayMode.OverAppWindow, loaded.Settings.OverlayMode);
    }

    [Fact]
    public void Existing_original_files_are_used_instead_of_extracting_copies()
    {
        string wav = WriteWav("original.wav");
        (AppSettings settings, SoundBoard board, _) = Setup(wav);
        string zip = Path.Combine(temp, "backup.zip");

        ConfigBackup.Save(zip, settings, board, includeAudio: true);
        LoadedBackup loaded = ConfigBackup.Load(zip, RestoreFolder);

        Assert.Equal(wav, Assert.Single(loaded.Board.Sounds).FilePath);
        Assert.Equal(0, loaded.SoundsRestoredFromBackup);
        Assert.False(Directory.Exists(RestoreFolder));
    }

    [Fact]
    public void Without_audio_a_missing_file_is_reported()
    {
        string wav = WriteWav("original.wav");
        (AppSettings settings, SoundBoard board, _) = Setup(wav);
        string zip = Path.Combine(temp, "backup.zip");

        ConfigBackup.Save(zip, settings, board, includeAudio: false);
        File.Delete(wav);
        LoadedBackup loaded = ConfigBackup.Load(zip, RestoreFolder);

        Assert.Equal([SoundName], loaded.MissingSounds);
        Assert.True(Assert.Single(loaded.Board.Sounds).IsMissing);
    }

    [Fact]
    public void Paths_in_the_user_profile_are_stored_without_the_user_name()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string path = Path.Combine(profile, "Desktop", "sounds", "nope.wav"); // never created
        (AppSettings settings, SoundBoard board, _) = Setup(path);
        string zip = Path.Combine(temp, "backup.zip");

        ConfigBackup.Save(zip, settings, board, includeAudio: false);

        using ZipArchive archive = ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry("board.json")!.Open());
        string json = reader.ReadToEnd();
        Assert.Contains("%USERPROFILE%", json);
        Assert.DoesNotContain(Path.GetFileName(profile), json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Files_that_are_not_backups_are_rejected()
    {
        string notZip = Path.Combine(temp, "notes.zip");
        File.WriteAllText(notZip, "hello");
        Assert.Throws<InvalidDataException>(() => ConfigBackup.Load(notZip, RestoreFolder));

        string otherZip = Path.Combine(temp, "other.zip");
        using (ZipArchive zip = ZipFile.Open(otherZip, ZipArchiveMode.Create))
        {
            zip.CreateEntry("readme.txt");
        }
        Assert.Throws<InvalidDataException>(() => ConfigBackup.Load(otherZip, RestoreFolder));
    }

    [Fact]
    public void Backups_from_a_newer_version_are_rejected()
    {
        string zip = HandMadeBackup(version: 99, new SoundBoard(), extraEntry: null);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ConfigBackup.Load(zip, RestoreFolder));
        Assert.Contains("newer", error.Message);
    }

    [Fact]
    public void Only_audio_files_are_ever_extracted()
    {
        var board = new SoundBoard();
        var sound = new SoundItem { Name = "sneaky", FilePath = Path.Combine(temp, "gone.wav") };
        board.Sounds.Add(sound);
        string zip = HandMadeBackup(ConfigBackup.FormatVersion, board, extraEntry: $"sounds/{sound.Id:N}.exe");

        LoadedBackup loaded = ConfigBackup.Load(zip, RestoreFolder);

        Assert.Equal(["sneaky"], loaded.MissingSounds);
        Assert.False(Directory.Exists(RestoreFolder));
    }

    [Fact]
    public void Automatic_backups_keep_only_the_newest()
    {
        string folder = Path.Combine(temp, "backups");
        (AppSettings settings, SoundBoard board, _) = Setup(WriteWav("a.wav"));

        for (int i = 0; i < 5; i++)
        {
            ConfigBackup.SaveAutomatic(folder, settings, board, $"r{i}", keep: 3);
        }

        IReadOnlyList<FileInfo> kept = ConfigBackup.AutomaticBackups(folder);
        Assert.Equal(3, kept.Count);
        Assert.DoesNotContain(kept, f => f.Name.EndsWith("-r0.zip") || f.Name.EndsWith("-r1.zip"));
    }

    private static (AppSettings Settings, SoundBoard Board, SoundItem Sound) Setup(string soundPath)
    {
        var settings = new AppSettings
        {
            Deadzone = 75,
            AutoCancelSeconds = 2.5,
            CancelKey = VirtualKeys.Mouse5,
            OverlayMode = OverlayMode.OverAppWindow,
        };
        var board = new SoundBoard();
        var sound = new SoundItem { Name = SoundName, FilePath = soundPath, Volume = 0.7f, DurationSeconds = 1.9 };
        board.Sounds.Add(sound);
        var wheel = new WheelDefinition { Name = "Wheel 1", Hotkey = VirtualKeys.Mouse4, Root = WheelPage.Create(16) };
        wheel.Root.Slots[3].MakeFolder("Sent 1", 16);
        wheel.Root.Slots[3].Folder!.Slots[0].AssignSound(sound.Id);
        board.Wheels.Add(wheel);
        return (settings, board, sound);
    }

    private string WriteWav(string name)
    {
        string path = Path.Combine(temp, name);
        using var writer = new WaveFileWriter(path, EngineFormat.WaveFormat);
        float[] samples = CachedSound.CreateTestTone(seconds: 0.1).Samples;
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private string HandMadeBackup(int version, SoundBoard board, string? extraEntry)
    {
        string path = Path.Combine(temp, $"handmade-{Guid.NewGuid():N}.zip");
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(zip, "manifest.json", new BackupManifest(ConfigBackup.FormatName, version, DateTime.UtcNow, null, true, board.Sounds.Count, board.Wheels.Count, 1));
        Write(zip, "settings.json", new AppSettings());
        Write(zip, "board.json", board);
        if (extraEntry is not null)
        {
            using Stream stream = zip.CreateEntry(extraEntry).Open();
            stream.Write([0x4D, 0x5A, 0x90, 0x00]);
        }
        return path;
    }

    private static void Write<T>(ZipArchive zip, string name, T value)
    {
        using Stream stream = zip.CreateEntry(name).Open();
        JsonSerializer.Serialize(stream, value, JsonFile.Options);
    }
}
