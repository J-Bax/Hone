using Hone.Core.Models;

namespace Hone.Diagnostics.Collection;

/// <summary>
/// Aggregate result of exporting data from all collectors in a diagnostic pass.
/// </summary>
public sealed record CollectionExportResult(
    bool Success,
    IReadOnlyDictionary<string, CollectorExportResult> CollectorData);
