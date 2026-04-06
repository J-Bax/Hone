namespace Hone.Reporting.Rca;

/// <summary>
/// Runtime counter metrics snapshot (CPU, memory) for a single run.
/// </summary>
internal sealed record CounterSnapshot(
    double CpuAvg,
    double CpuMax,
    double WorkingSetMBAvg,
    double WorkingSetMBMax);
