namespace Hone.Core.Models;

/// <summary>
/// Result of exporting collector data.
/// </summary>
public sealed record CollectorExport(
    bool Success,
    IReadOnlyList<string> ExportedPaths,
    string? Summary)
{
    /// <summary>
    /// Gets the exported paths, defaulting to an empty list.
    /// </summary>
    public IReadOnlyList<string> ExportedPaths { get; init; } = ExportedPaths ?? [];
}
