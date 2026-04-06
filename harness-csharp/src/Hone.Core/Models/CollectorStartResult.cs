namespace Hone.Core.Models;

/// <summary>
/// Result of starting a collector session.
/// </summary>
public sealed record CollectorStartResult(
    bool Success,
    object? Handle = null,
    string? Error = null);
