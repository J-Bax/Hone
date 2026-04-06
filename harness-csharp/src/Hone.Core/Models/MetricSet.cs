namespace Hone.Core.Models;

/// <summary>
/// Structured k6 summary metrics for a single run.
/// </summary>
public sealed record MetricSet(
    string Timestamp,
    int Experiment,
    int Run,
    HttpReqDurationMetrics HttpReqDuration,
    HttpReqCountMetrics HttpReqs,
    HttpReqFailedMetrics HttpReqFailed,
    string? SummaryPath);
