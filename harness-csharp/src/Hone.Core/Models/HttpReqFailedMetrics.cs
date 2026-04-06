namespace Hone.Core.Models;

/// <summary>
/// HTTP request failure metrics from a k6 summary.
/// </summary>
public sealed record HttpReqFailedMetrics(
    long Count,
    double Rate);
