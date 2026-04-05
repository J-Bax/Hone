using Hone.Core.Models;

namespace Hone.Orchestration.Loop;

internal sealed class LoopState
{
    internal MetricSet? PreviousMetrics { get; set; }
    internal int PreviousMetricsExperiment { get; set; }
    internal double BestP95 { get; set; }
    internal int BestExperiment { get; set; }
    internal int StaleCount { get; set; }
    internal int ConsecutiveFailures { get; set; }
    internal int SuccessCount { get; set; }
    internal string CurrentBranch { get; set; } = "main";
    internal List<int> PrChain { get; } = [];
    internal List<string> BranchChain { get; } = [];
    internal List<int> FailedExperiments { get; } = [];
    internal string ExitReason { get; set; } = "max_experiments";
}
