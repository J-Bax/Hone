using Hone.Core.Models;

namespace Hone.Diagnostics.Measurement;

/// <summary>
/// Result of a single collection pass (one group of collectors).
/// </summary>
public sealed record CollectionPassResult(
    bool Success,
    IReadOnlyDictionary<string, CollectorExportResult> CollectorData);
