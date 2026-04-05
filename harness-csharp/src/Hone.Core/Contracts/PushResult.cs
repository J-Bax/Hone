namespace Hone.Core.Contracts;

/// <summary>
/// Result of pushing a branch to a remote code host.
/// </summary>
public sealed record PushResult(
    bool Success,
    string? Output);
