using Hone.Core.Models;

namespace Hone.Core.Contracts;

/// <summary>
/// Result of a load test execution.
/// </summary>
public sealed record LoadTestResult(
    bool Success,
    MetricSet? Metrics,
    string? SummaryPath,
    string? Output);
