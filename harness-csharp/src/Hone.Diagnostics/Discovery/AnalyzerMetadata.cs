namespace Hone.Diagnostics.Discovery;

/// <summary>
/// Metadata parsed from an analyzer's analyzer.yaml manifest.
/// </summary>
public sealed record AnalyzerMetadata(
    string Name,
    string? Description,
    IReadOnlyList<string> RequiredCollectors,
    IReadOnlyList<string>? OptionalCollectors,
    string? AgentName,
    IReadOnlyDictionary<string, object?> DefaultSettings)
{
    /// <summary>
    /// Gets the required collectors, defaulting to empty.
    /// </summary>
    public IReadOnlyList<string> RequiredCollectors { get; init; } = RequiredCollectors ?? [];

    /// <summary>
    /// Gets the default settings, defaulting to empty.
    /// </summary>
    public IReadOnlyDictionary<string, object?> DefaultSettings { get; init; } =
        DefaultSettings ?? new Dictionary<string, object?>(StringComparer.Ordinal);
}
