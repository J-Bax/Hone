using Hone.Core.Models;

namespace Hone.Orchestration.Loop;

/// <summary>
/// Input for the analysis pipeline step.
/// </summary>
internal sealed record AnalysisInput(
    string TargetDir,
    int Experiment,
    MetricSet BaselineMetrics,
    MetricSet? ReferenceMetrics);

/// <summary>
/// Result of the analysis pipeline step.
/// </summary>
internal sealed record AnalysisResult(
    bool Success,
    IReadOnlyList<Opportunity> Opportunities)
{
    /// <summary>Gets the discovered opportunities, defaulting to an empty list.</summary>
    public IReadOnlyList<Opportunity> Opportunities { get; init; } = Opportunities ?? [];
}

/// <summary>
/// Input for optional queue-item classification.
/// </summary>
internal sealed record ClassificationInput(
    string FilePath,
    string Explanation,
    int Experiment,
    string TargetDir);

/// <summary>
/// Result of classifying a queue item.
/// </summary>
internal sealed record ClassificationResult(
    bool Success,
    OpportunityScope Scope);

/// <summary>
/// Input for the load-test verification step.
/// </summary>
internal sealed record LoadTestInput(
    string TargetDir,
    int Experiment,
    string ResultsPath);
