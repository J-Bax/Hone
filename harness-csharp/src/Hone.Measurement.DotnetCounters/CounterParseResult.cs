using Hone.Measurement.Comparison;

namespace Hone.Measurement.DotnetCounters;

/// <summary>
/// Result of parsing a dotnet-counters CSV file.
/// </summary>
/// <param name="Counters">Flattened counter values keyed by <c>"MetricName.Statistic"</c> (e.g. <c>"CpuUsage.Avg"</c>).</param>
/// <param name="StructuredMetrics">Full structured metrics for the efficiency tiebreaker, or <see langword="null"/> if no data.</param>
public sealed record CounterParseResult(
    IReadOnlyDictionary<string, double> Counters,
    RuntimeCounterMetrics? StructuredMetrics);
