namespace Hone.Diagnostics.Discovery;

/// <summary>
/// A fully resolved analyzer ready to participate in a diagnostic run.
/// </summary>
public sealed record DiscoveredAnalyzer(
    string Name,
    string Directory,
    AnalyzerMetadata Metadata,
    IReadOnlyDictionary<string, object?> MergedSettings);
