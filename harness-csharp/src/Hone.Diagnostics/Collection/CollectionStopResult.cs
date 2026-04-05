namespace Hone.Diagnostics.Collection;

/// <summary>
/// Aggregate result of stopping all collectors in a diagnostic pass.
/// </summary>
public sealed record CollectionStopResult(
    bool Success,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ArtifactMap);
