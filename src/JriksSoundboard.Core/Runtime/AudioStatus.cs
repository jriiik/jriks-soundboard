namespace JriksSoundboard.Core.Runtime;

/// <summary>One-line audio health for the status bar.</summary>
public sealed record AudioStatus(bool IsOk, string Message);
