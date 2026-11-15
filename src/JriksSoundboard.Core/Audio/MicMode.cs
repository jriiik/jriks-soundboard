namespace JriksSoundboard.Core.Audio;

/// <summary>How your real microphone reaches the virtual mic.</summary>
public enum MicMode
{
    /// <summary>
    /// Mic always passed through; you hold the game's own push-to-talk key (default).
    /// Sounds only go out while you hold it.
    /// </summary>
    GamePushToTalk,

    /// <summary>Game set to open mic; the app only passes your mic while you hold its push-to-talk key.</summary>
    AppPushToTalk,

    /// <summary>Game set to open mic; the app passes your mic while you speak above a threshold.</summary>
    VoiceActivation,

    /// <summary>Only soundboard sounds reach the game.</summary>
    Off,
}
