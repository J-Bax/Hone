namespace Hone.Agents.Loop.Analysis;

/// <summary>
/// Aggregated context supplied to the analysis agent prompt.
/// </summary>
public sealed record AnalysisContext(
    IReadOnlyList<string> SourceFilePaths,
    string CounterContext,
    string TrafficContext,
    string HistoryContext,
    string ProfilingContext);
