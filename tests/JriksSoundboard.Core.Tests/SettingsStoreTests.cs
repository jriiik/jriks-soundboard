using JriksSoundboard.Core.Audio;
using JriksSoundboard.Core.Input;
using JriksSoundboard.Core.Settings;

namespace JriksSoundboard.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "jriks-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_gives_the_agreed_defaults()
    {
        AppSettings settings = SettingsStore.Load(Path.Combine(tempDir, "settings.json"));

        Assert.Equal(MicMode.GamePushToTalk, settings.MicMode);   // you hold CS2's own push-to-talk
        Assert.Equal(OverlayMode.OnTopOfGame, settings.OverlayMode);
        Assert.True(settings.AutoCancelEnabled);
        Assert.Equal(3.0, settings.AutoCancelSeconds);
        Assert.Equal(60, settings.Deadzone);                       // flick feel confirmed in CS2
        Assert.Equal(240, settings.Radius);
        Assert.Equal(VirtualKeys.None, settings.StopKey);
        Assert.Null(settings.GameOutputDeviceId);
    }

    [Fact]
    public void Save_then_load_round_trips_and_stores_enums_as_names()
    {
        string path = Path.Combine(tempDir, "nested", "settings.json");
        var original = new AppSettings
        {
            GameOutputDeviceId = "{0.0.0.00000000}.{cable}",
            MicMode = MicMode.VoiceActivation,
            VoiceThresholdDb = -35,
            OverlayMode = OverlayMode.OverAppWindow,
            StopKey = VirtualKeys.Mouse5,
            AutoCancelSeconds = 1.5,
            SoundVolume = 0.8f,
        };

        SettingsStore.Save(original, path);
        AppSettings loaded = SettingsStore.Load(path);

        Assert.Contains("\"VoiceActivation\"", File.ReadAllText(path));
        Assert.Equal(original.GameOutputDeviceId, loaded.GameOutputDeviceId);
        Assert.Equal(MicMode.VoiceActivation, loaded.MicMode);
        Assert.Equal(-35, loaded.VoiceThresholdDb);
        Assert.Equal(OverlayMode.OverAppWindow, loaded.OverlayMode);
        Assert.Equal(VirtualKeys.Mouse5, loaded.StopKey);
        Assert.Equal(1.5, loaded.AutoCancelSeconds);
        Assert.Equal(0.8f, loaded.SoundVolume);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Old_test_bench_settings_still_load()
    {
        Directory.CreateDirectory(tempDir);
        string path = Path.Combine(tempDir, "settings.json");
        File.WriteAllText(path, """{ "Version": 1, "WheelKey": 5, "TestSoundPath": "C:\\x.mp3", "MicPassthrough": true, "Deadzone": 70 }""");

        AppSettings settings = SettingsStore.Load(path);

        Assert.Equal(70, settings.Deadzone);
        Assert.Equal(MicMode.GamePushToTalk, settings.MicMode);
    }

    [Fact]
    public void Corrupt_file_throws_instead_of_silently_resetting()
    {
        Directory.CreateDirectory(tempDir);
        string path = Path.Combine(tempDir, "settings.json");
        File.WriteAllText(path, "{ not json");

        Assert.ThrowsAny<Exception>(() => SettingsStore.Load(path));
    }

    [Fact]
    public void Wheel_options_reflect_auto_cancel_choice()
    {
        var settings = new AppSettings { AutoCancelEnabled = true, AutoCancelSeconds = 2 };
        Assert.Equal(TimeSpan.FromSeconds(2), settings.ToWheelOptions().AutoCancelAfter);

        settings.AutoCancelEnabled = false;
        Assert.Null(settings.ToWheelOptions().AutoCancelAfter);
    }
}
