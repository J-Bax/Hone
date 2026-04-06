using Hone.Core.Models;

namespace Hone.Orchestration.Loop;

internal sealed record AnalysisInput(
    string TargetDir,
    int Experiment,
    MetricSet BaselineMetrics,
    MetricSet? ReferenceMetrics);

internal sealed record AnalysisResult(
    bool Success,
    IReadOnlyList<Opportunity> Opportunities);

internal sealed record ClassificationInput(
    string FilePath,
    string Explanation,
    int Experiment,
    string TargetDir);

internal sealed record ClassificationResult(
    bool Success,
    OpportunityScope Scope);

internal sealed record LoadTestInput(
    string TargetDir,
    int Experiment,
    string ResultsPath);
