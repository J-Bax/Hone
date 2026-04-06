namespace Hone.Reporting.Console;

/// <summary>
/// Counter metrics (CPU, memory) for a single experiment shown in the results table.
/// </summary>
public sealed record ConsoleCounterData(
    double? CpuAvgPercent,
    double? MemoryMB);
