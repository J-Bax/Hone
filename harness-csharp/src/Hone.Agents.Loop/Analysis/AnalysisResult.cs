using Hone.Core.Models;

namespace Hone.Agents.Loop.Analysis;

/// <summary>
/// Result of the analysis agent invocation.
/// </summary>
public sealed record AnalysisResult(
    bool Success,
    string? FilePath,
    string? Explanation,
    IReadOnlyList<Opportunity> Opportunities,
    string Prompt,
    string Response);
