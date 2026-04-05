namespace Hone.Orchestration.Loop;

/// <summary>
/// Summary returned by <see cref="HoneLoopRunner.RunAsync"/> after the optimization loop completes.
/// </summary>
internal sealed record LoopResult(
    string ExitReason,
    int ExperimentsRun,
    int SuccessCount,
    double BestP95,
    int BestExperiment,
    double BaselineP95,
    IReadOnlyList<int> PrChain,
    IReadOnlyList<string> BranchChain,
    IReadOnlyList<int> FailedExperiments)
{
    /// <summary>Gets the PR chain, defaulting to an empty list.</summary>
    public IReadOnlyList<int> PrChain { get; init; } = PrChain ?? [];

    /// <summary>Gets the branch chain, defaulting to an empty list.</summary>
    public IReadOnlyList<string> BranchChain { get; init; } = BranchChain ?? [];

    /// <summary>Gets the failed experiment numbers, defaulting to an empty list.</summary>
    public IReadOnlyList<int> FailedExperiments { get; init; } = FailedExperiments ?? [];
}
