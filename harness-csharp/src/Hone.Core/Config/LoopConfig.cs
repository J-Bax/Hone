namespace Hone.Core.Config;

/// <summary>
/// Configuration for the agentic optimization loop.
/// </summary>
public sealed record LoopConfig(
    int MaxExperiments = 999,
    string BranchPrefix = "hone/experiment",
    bool StackedDiffs = true,
    bool WaitForMerge = false,
    bool SkipClassification = false,
    int ExperimentCooldownSeconds = 30,
    int MaxHistoryExperiments = 10);
