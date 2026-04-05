namespace Hone.Core.Contracts;

/// <summary>
/// Result of stopping and parsing runtime metrics collection.
/// </summary>
public sealed record RuntimeMetricsResult(
    bool Success,
    IReadOnlyDictionary<string, double>? Counters);
