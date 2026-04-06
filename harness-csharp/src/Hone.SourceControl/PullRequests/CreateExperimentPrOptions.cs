namespace Hone.SourceControl.PullRequests;

/// <summary>
/// Options for creating an experiment PR.
/// </summary>
/// <param name="Experiment">The experiment number.</param>
/// <param name="BranchName">The head branch for the PR.</param>
/// <param name="BaseBranch">The base branch the PR targets.</param>
/// <param name="Outcome">The experiment outcome (e.g. "improved", "regressed").</param>
/// <param name="Description">A human-readable description of the experiment.</param>
/// <param name="Body">The full markdown body for the PR.</param>
/// <param name="IsDryRun">Whether this is a dry-run experiment.</param>
/// <param name="WorkingDirectory">Optional working directory for the code host CLI.</param>
public sealed record CreateExperimentPrOptions(
    int Experiment,
    string BranchName,
    string BaseBranch,
    string Outcome,
    string Description,
    string Body,
    bool IsDryRun,
    string? WorkingDirectory = null);
