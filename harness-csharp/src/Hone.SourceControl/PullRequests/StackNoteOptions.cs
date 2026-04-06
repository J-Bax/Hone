namespace Hone.SourceControl.PullRequests;

/// <summary>
/// Options for building a PR stack note.
/// </summary>
/// <param name="PrChain">The ordered list of PRs in the stacked-diff chain.</param>
/// <param name="FailedExperiments">Experiments that were attempted but did not produce branches.</param>
/// <param name="Experiment">The current experiment number.</param>
/// <param name="OutcomeTag">The outcome tag for the current experiment (e.g. "ACCEPTED", "REJECTED").</param>
/// <param name="BaseBranch">The base branch at the root of the chain.</param>
public sealed record StackNoteOptions(
    IReadOnlyList<PrChainEntry> PrChain,
    IReadOnlyList<FailedExperimentEntry> FailedExperiments,
    int Experiment,
    string OutcomeTag,
    string BaseBranch);
