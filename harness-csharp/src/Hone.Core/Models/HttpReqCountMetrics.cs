namespace Hone.Core.Models;

/// <summary>
/// HTTP request count metrics from a k6 summary.
/// </summary>
public sealed record HttpReqCountMetrics(
    long Count,
    double Rate);
