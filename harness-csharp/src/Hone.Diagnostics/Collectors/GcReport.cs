namespace Hone.Diagnostics.Collectors;

/// <summary>
/// Structured GC statistics report matching the JSON format produced by
/// the PerfView GC collector export.
/// </summary>
internal sealed class GcReport
{
    public GenerationStatsReport GenerationStats { get; set; } = new();

    public HeapStatsReport HeapStats { get; set; } = new();

    public PauseStatsReport PauseStats { get; set; } = new();

    public AllocationStatsReport AllocationStats { get; set; } = new();
}

/// <summary>Per-generation GC statistics.</summary>
internal sealed class GenerationStatsReport
{
    public GenerationInfo Gen0 { get; set; } = new();

    public GenerationInfo Gen1 { get; set; } = new();

    public GenerationInfo Gen2 { get; set; } = new();
}

/// <summary>Statistics for a single GC generation.</summary>
internal sealed class GenerationInfo
{
    public int Count { get; set; }

    public double AvgPauseMs { get; set; }

    public double MaxPauseMs { get; set; }
}

/// <summary>Heap size and allocation statistics.</summary>
internal sealed class HeapStatsReport
{
    public double PeakSizeMB { get; set; }

    public double TotalAllocMB { get; set; }

    public double FragmentationPct { get; set; }
}

/// <summary>GC pause time statistics.</summary>
internal sealed class PauseStatsReport
{
    public double TotalPauseMs { get; set; }

    public double MaxPauseMs { get; set; }

    public double GcPauseRatio { get; set; }
}

/// <summary>Allocation rate and top type statistics.</summary>
internal sealed class AllocationStatsReport
{
    public double AllocRateMBSec { get; set; }

    public IReadOnlyList<string> TopTypes { get; set; } = [];
}
