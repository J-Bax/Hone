namespace Hone.Diagnostics.Collection;

/// <summary>
/// Aggregate result of starting all collectors in a diagnostic pass.
/// </summary>
public sealed record CollectionStartResult(
    bool Success,
    IReadOnlyDictionary<string, object> Handles);
