namespace Hone.Reporting.PullRequest;

/// <summary>
/// Options for building an experiment PR body.
/// Pre-built string sections are passed in so the builder is a pure template.
/// </summary>
internal sealed record PrBodyOptions
{
    /// <summary>Whether this is an accepted or rejected experiment PR.</summary>
    public required PrBodyType Type { get; init; }

    /// <summary>Experiment number.</summary>
    public required int Experiment { get; init; }

    /// <summary>Brief description of the optimization attempted.</summary>
    public required string Description { get; init; }

    /// <summary>The file that was modified.</summary>
    public required string FilePath { get; init; }

    /// <summary>Pre-built stack context note (includes trailing newline if non-empty).</summary>
    public string StackNote { get; init; } = string.Empty;

    /// <summary>Pre-built dry-run notice string (includes trailing newline if non-empty).</summary>
    public string DryRunNotice { get; init; } = string.Empty;

    /// <summary>Pre-built metrics comparison table.</summary>
    public string MetricsSection { get; init; } = string.Empty;

    /// <summary>Pre-built root cause analysis section.</summary>
    public string RcaSection { get; init; } = string.Empty;

    /// <summary>(Rejected only) Emoji + bold label for the rejection reason.</summary>
    public string OutcomeLabel { get; init; } = string.Empty;

    /// <summary>(Rejected only) Additional outcome detail text.</summary>
    public string OutcomeDetail { get; init; } = string.Empty;

    /// <summary>(Accepted only) Overall improvement percentage vs baseline.</summary>
    public string ImprovementPct { get; init; } = string.Empty;

    /// <summary>(Accepted only) Pre-built per-scenario breakdown table.</summary>
    public string ScenarioBreakdown { get; init; } = string.Empty;

    /// <summary>Optional retry-attempt summary for iterative fixer experiments.</summary>
    public string IterationSummary { get; init; } = string.Empty;
}
