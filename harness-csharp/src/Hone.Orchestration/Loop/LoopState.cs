using Hone.Core.Models;

namespace Hone.Orchestration.Loop;

/// <summary>
/// Mutable state tracker for the optimization loop.
/// Tracks metrics references, counters, branch chain, and exit conditions.
/// </summary>
internal sealed class LoopState
{
    /// <summary>Gets or sets the metrics from the most recent successful experiment.</summary>
    internal MetricSet? PreviousMetrics { get; set; }

    /// <summary>Gets or sets which experiment produced <see cref="PreviousMetrics"/>.</summary>
    internal int PreviousMetricsExperiment { get; set; }

    /// <summary>Gets or sets the best P95 latency observed so far.</summary>
    internal double BestP95 { get; set; }

    /// <summary>Gets or sets the experiment that achieved <see cref="BestP95"/>.</summary>
    internal int BestExperiment { get; set; }

    /// <summary>Gets or sets the count of consecutive stale experiments.</summary>
    internal int StaleCount { get; set; }

    /// <summary>Gets or sets the count of consecutive failures (any non-improvement).</summary>
    internal int ConsecutiveFailures { get; set; }

    /// <summary>Gets or sets the total number of accepted experiments.</summary>
    internal int SuccessCount { get; set; }

    /// <summary>Gets or sets the branch name of the last successful experiment.</summary>
    internal string CurrentBranch { get; set; } = "main";

    /// <summary>Gets the PR numbers from accepted experiments (stacked order).</summary>
    internal List<int> PrChain { get; } = [];

    /// <summary>Gets the branch names from accepted experiments (stacked order).</summary>
    internal List<string> BranchChain { get; } = [];

    /// <summary>Gets the experiment numbers that failed.</summary>
    internal List<int> FailedExperiments { get; } = [];

    /// <summary>Gets or sets the reason the loop exited.</summary>
    internal string ExitReason { get; set; } = "max_experiments";
}
