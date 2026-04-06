using Hone.Core.Models;

namespace Hone.Measurement.Orchestration;

/// <summary>
/// Result of a multi-run scale test orchestration.
/// </summary>
public sealed record ScaleTestResult(
    bool Success,
    MetricSet? Metrics,
    string? SummaryPath,
    int RunCount,
    IReadOnlyList<MetricSet> RunMetrics);
