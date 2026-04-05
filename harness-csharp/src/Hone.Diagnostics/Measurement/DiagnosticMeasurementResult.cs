using Hone.Core.Models;

namespace Hone.Diagnostics.Measurement;

/// <summary>
/// Final combined result of the full diagnostic measurement pipeline:
/// multi-pass collection followed by analyzer execution.
/// </summary>
/// <remarks>
/// This record is assembled by the caller (loop runner), not by the orchestrator itself.
/// The caller runs each collection pass via <see cref="DiagnosticMeasurementOrchestrator.RunCollectionPassAsync"/>,
/// merges the per-pass <see cref="CollectionPassResult.CollectorData"/> dictionaries, then feeds
/// the merged data into <see cref="DiagnosticMeasurementOrchestrator.RunAnalyzersAsync"/> to produce
/// analyzer reports. This type represents the complete diagnostic pipeline output.
/// </remarks>
public sealed record DiagnosticMeasurementResult(
    bool Success,
    IReadOnlyDictionary<string, CollectorExportResult> CollectorData,
    IReadOnlyDictionary<string, AnalyzerResult> AnalyzerReports);
