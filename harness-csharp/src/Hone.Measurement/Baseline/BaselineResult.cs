using Hone.Core.Models;
using Hone.Measurement.Orchestration;

namespace Hone.Measurement.Baseline;

/// <summary>
/// Result of a baseline measurement run.
/// </summary>
public sealed record BaselineResult(
    bool Success,
    MetricSet? Metrics,
    IReadOnlyDictionary<string, double>? CounterMetrics,
    ScaleTestResult? ScaleTestDetail);
