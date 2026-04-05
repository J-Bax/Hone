namespace Hone.Core.Models;

/// <summary>
/// Artifacts produced when stopping a data collector.
/// </summary>
public sealed record CollectorArtifacts(
    bool Success,
    IReadOnlyList<string> ArtifactPaths)
{
    /// <summary>
    /// Gets the artifact paths, defaulting to an empty list.
    /// </summary>
    public IReadOnlyList<string> ArtifactPaths { get; init; } = ArtifactPaths ?? [];
}
