namespace Hone.Core.Models;

/// <summary>
/// Context passed to analyzer plugins containing collector outputs and run metadata.
/// </summary>
public sealed record AnalyzerContext(
    IReadOnlyDictionary<string, CollectorExportResult> CollectorData,
    MetricSet? CurrentMetrics,
    int Experiment,
    IReadOnlyDictionary<string, object?> Settings,
    string OutputDir);
