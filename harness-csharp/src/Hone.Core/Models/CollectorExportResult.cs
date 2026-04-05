namespace Hone.Core.Models;

/// <summary>
/// Result of exporting collector data into analysis-ready output.
/// </summary>
public sealed record CollectorExportResult(
    bool Success,
    IReadOnlyList<string> ExportedPaths,
    string? Summary = null,
    IReadOnlyDictionary<string, object?>? ExtraProperties = null)
{
    public IReadOnlyList<string> ExportedPaths { get; init; } = ExportedPaths;
}
