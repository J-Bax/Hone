namespace Hone.Core.Models;

/// <summary>
/// Artifacts produced when stopping a data collector.
/// </summary>
public sealed record CollectorArtifacts(
    bool Success,
    IReadOnlyList<string> ArtifactPaths)
{
    public IReadOnlyList<string> ArtifactPaths { get; init; } = ArtifactPaths;
}
