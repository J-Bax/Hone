namespace Hone.Core.Models;

/// <summary>
/// HTTP request duration metrics from a k6 summary.
/// </summary>
public sealed record HttpReqDurationMetrics(
    double Avg,
    double P50,
    double P90,
    double P95,
    double P99,
    double Max);
