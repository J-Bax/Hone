namespace Hone.Diagnostics.Discovery;

/// <summary>
/// Metadata parsed from a collector's collector.yaml manifest.
/// </summary>
public sealed record CollectorMetadata(
    string Name,
    string? Description,
    string Group,
    bool RequiresAdmin,
    string? OverheadImpact,
    IReadOnlyDictionary<string, object?> DefaultSettings)
{
    /// <summary>
    /// Gets the collector group, defaulting to "default".
    /// </summary>
    public string Group { get; init; } = Group ?? "default";

    /// <summary>
    /// Gets the default settings, defaulting to empty.
    /// </summary>
    public IReadOnlyDictionary<string, object?> DefaultSettings { get; init; } =
        DefaultSettings ?? new Dictionary<string, object?>(StringComparer.Ordinal);
}
