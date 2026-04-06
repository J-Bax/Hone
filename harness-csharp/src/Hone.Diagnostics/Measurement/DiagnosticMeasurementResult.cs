using Hone.Core.Models;

namespace Hone.Diagnostics.Measurement;

/// <summary>
/// Final combined result of the full diagnostic measurement pipeline:
/// multi-pass collection followed by analyzer execution.
/// </summary>
public sealed record DiagnosticMeasurementResult(
    bool Success,
    IReadOnlyDictionary<string, CollectorExportResult> CollectorData,
    IReadOnlyDictionary<string, AnalyzerResult> AnalyzerReports);
