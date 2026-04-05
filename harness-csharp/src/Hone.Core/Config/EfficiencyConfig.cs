namespace Hone.Core.Config;

/// <summary>
/// Efficiency tiebreaker settings — accepts flat-performance experiments
/// when OS-level resource usage decreased.
/// </summary>
public sealed record EfficiencyConfig(
    bool Enabled = true,
    double MinCpuReductionPct = 0.05,
    double MinWorkingSetReductionPct = 0.05);
