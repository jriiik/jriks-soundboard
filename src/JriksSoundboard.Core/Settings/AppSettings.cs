using JriksSoundboard.Core.Audio;
using JriksSoundboard.Core.Input;
using JriksSoundboard.Core.Wheel;

namespace JriksSoundboard.Core.Settings;

/// <summary>Where the wheel pops up.</summary>
public enum OverlayMode
{
    /// <summary>Over the game window. Only visible when the game runs windowed / fullscreen-windowed.</summary>
    OnTopOfGame,

    /// <summary>Over this app's own window (put it on your second monitor).</summary>
    OverAppWindow,

    /// <summary>No overlay at all (you rely on muscle memory).</summary>
    Off,
}

/// <summary>User preferences, saved as JSON in %APPDATA%\JriksSoundboard\settings.json.</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 2;

    // ── Audio routing ──
    public string? GameOutputDeviceId { get; set; }
    public string? MonitorDeviceId { get; set; }
    public bool MonitorEnabled { get; set; } = true;
    public string? MicDeviceId { get; set; }

    // ── Microphone ──
    public MicMode MicMode { get; set; } = MicMode.GamePushToTalk;
    public int PushToTalkKey { get; set; } = VirtualKeys.None;
    public float VoiceThresholdDb { get; set; } = -40f;

    // ── Levels (1.0 = original) ──
    public float SoundVolume { get; set; } = 1.0f;
    public float MonitorVolume { get; set; } = 0.6f;
    public float MicVolume { get; set; } = 1.0f;
    public bool NormalizeLoudness { get; set; } = true;
    public bool StopPreviousOnPlay { get; set; } = true;

    // ── Wheel behaviour ──
    public bool AutoCancelEnabled { get; set; } = true;
    public double AutoCancelSeconds { get; set; } = 3.0;
    public bool TimeoutCue { get; set; } = true;
    public int FolderDwellMs { get; set; } = 350;
    public double Deadzone { get; set; } = 60;
    public double Radius { get; set; } = 240;

    // ── Overlay ──
    public OverlayMode OverlayMode { get; set; } = OverlayMode.OnTopOfGame;
    public double OverlaySize { get; set; } = 380;
    public double OverlayOpacity { get; set; } = 0.95;
    public bool ShowPlaybackTimer { get; set; } = true;

    // ── Hotkeys (wheel hotkeys live on each wheel in board.json) ──
    public int StopKey { get; set; } = VirtualKeys.None;

    public WheelOptions ToWheelOptions() => new(
        AutoCancelEnabled ? TimeSpan.FromSeconds(Math.Max(0.5, AutoCancelSeconds)) : null,
        TimeSpan.FromMilliseconds(Math.Max(0, FolderDwellMs)),
        Deadzone,
        Math.Max(Radius, Deadzone + 1));
}

public static class SettingsStore
{
    public static string DefaultPath => AppPaths.SettingsFile;

    /// <summary>Loads settings; returns defaults if the file is missing. Throws if it exists but is corrupt.</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        return File.Exists(path) ? JsonFile.Read<AppSettings>(path) : new AppSettings();
    }

    /// <summary>Writes atomically so a crash mid-save can't corrupt settings.</summary>
    public static void Save(AppSettings settings, string? path = null) =>
        JsonFile.WriteAtomic(path ?? DefaultPath, settings);
}
