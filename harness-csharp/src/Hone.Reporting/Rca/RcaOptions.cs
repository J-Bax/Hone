using Hone.Core.Models;

namespace Hone.Reporting.Rca;

/// <summary>
/// All inputs required to build a root cause analysis markdown document.
/// </summary>
internal sealed record RcaOptions
{
    /// <summary>Target file identified by the analysis agent.</summary>
    public required string FilePath { get; init; }

    /// <summary>Description of the proposed optimization.</summary>
    public required string Explanation { get; init; }

    /// <summary>Scope of the change (narrow vs architecture).</summary>
    public required OpportunityScope ChangeScope { get; init; }

    /// <summary>Reasoning behind the scope classification.</summary>
    public required string ScopeReasoning { get; init; }

    /// <summary>Optimized file content (optional).</summary>
    public string? CodeBlock { get; init; }

    /// <summary>Current experiment metrics.</summary>
    public required MetricSet CurrentMetrics { get; init; }

    /// <summary>Baseline metrics to compare against.</summary>
    public required MetricSet BaselineMetrics { get; init; }

    /// <summary>Result of comparing current vs baseline metrics.</summary>
    public ComparisonResult? ComparisonResult { get; init; }

    /// <summary>Estimated production impact (optional).</summary>
    public ImpactEstimate? ImpactEstimate { get; init; }

    /// <summary>Runtime counter metrics for the experiment run (optional).</summary>
    public CounterSnapshot? CounterMetrics { get; init; }

    /// <summary>Runtime counter metrics for the baseline run (optional).</summary>
    public CounterSnapshot? BaselineCounterMetrics { get; init; }

    /// <summary>Experiment number.</summary>
    public required int Experiment { get; init; }

    /// <summary>Pre-built iteration summary markdown section (optional).</summary>
    public string? IterationSummarySection { get; init; }

    /// <summary>
    /// Timestamp for the "Generated" line. Defaults to <see cref="DateTimeOffset.UtcNow"/> when null.
    /// </summary>
    public DateTimeOffset? GeneratedAtUtc { get; init; }
}
