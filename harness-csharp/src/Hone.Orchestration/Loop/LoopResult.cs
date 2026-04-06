namespace Hone.Orchestration.Loop;

internal sealed record LoopResult(
    string ExitReason,
    int ExperimentsRun,
    int SuccessCount,
    double BestP95,
    int BestExperiment,
    double BaselineP95,
    IReadOnlyList<int> PrChain,
    IReadOnlyList<string> BranchChain,
    IReadOnlyList<int> FailedExperiments);
