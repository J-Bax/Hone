namespace Hone.SourceControl.PullRequests;

/// <summary>
/// Represents a failed experiment that did not produce a branch.
/// </summary>
/// <param name="Experiment">The experiment number.</param>
/// <param name="Reason">The failure reason (e.g. "regressed", "build_failure").</param>
public sealed record FailedExperimentEntry(
    int Experiment,
    string Reason);
