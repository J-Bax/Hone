namespace Hone.SourceControl.PullRequests;

/// <summary>
/// Options for building a PR body.
/// </summary>
/// <param name="Experiment">The experiment number.</param>
/// <param name="Outcome">The experiment outcome (e.g. "improved", "regressed").</param>
/// <param name="Description">A human-readable description of the experiment.</param>
/// <param name="FilePath">The file that was modified by the experiment.</param>
/// <param name="RcaSummary">Root cause analysis summary text.</param>
/// <param name="ImprovementPercent">Percentage improvement (only meaningful for accepted experiments).</param>
/// <param name="ScenarioBreakdown">Markdown breakdown of per-scenario results.</param>
/// <param name="MetricsSummary">Formatted metrics summary text.</param>
/// <param name="IterationSummary">Formatted iteration summary text.</param>
/// <param name="StackNote">Pre-built stack note markdown (from <see cref="StackNoteBuilder"/>).</param>
/// <param name="DryRunNotice">Pre-built dry-run notice string.</param>
/// <param name="OutcomeLabel">Emoji + bold label for the rejection reason (rejected only).</param>
/// <param name="OutcomeDetail">Additional outcome detail text (rejected only).</param>
/// <param name="IsRevert">Whether the experiment code was reverted on the branch.</param>
public sealed record PrBodyOptions(
    int Experiment,
    string Outcome,
    string? Description,
    string? FilePath,
    string? RcaSummary,
    double? ImprovementPercent,
    string? ScenarioBreakdown,
    string? MetricsSummary,
    string? IterationSummary,
    string? StackNote,
    string? DryRunNotice,
    string? OutcomeLabel,
    string? OutcomeDetail,
    bool IsRevert);
