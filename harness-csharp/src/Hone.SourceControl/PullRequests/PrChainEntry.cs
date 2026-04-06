namespace Hone.SourceControl.PullRequests;

/// <summary>
/// Represents a single PR in the stacked-diff chain.
/// </summary>
/// <param name="Experiment">The experiment number associated with this PR.</param>
/// <param name="Number">The PR number on the code host.</param>
/// <param name="Outcome">The outcome of the experiment (e.g. "improved", "regressed").</param>
public sealed record PrChainEntry(
    int Experiment,
    int Number,
    string Outcome);
