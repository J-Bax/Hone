namespace Hone.Measurement.Comparison;

/// <summary>
/// Structured .NET runtime counter data used by the efficiency tiebreaker
/// and included in comparison output.
/// </summary>
public sealed record RuntimeCounterMetrics(
    CounterStatistic CpuUsage,
    CounterStatistic WorkingSetMB,
    CounterStatistic GcHeapSizeMB,
    CounterStatistic Gen0Collections,
    CounterStatistic Gen1Collections,
    CounterStatistic Gen2Collections,
    CounterStatistic GcPauseRatio,
    CounterStatistic ThreadPoolThreads,
    CounterStatistic ThreadPoolQueueLength,
    CounterStatistic ExceptionCount,
    CounterStatistic AllocRateMB);
