namespace Hone.Core.Models;

/// <summary>
/// Result of invoking the performance analyzer.
/// </summary>
public sealed record AnalyzerReport(
    bool Success,
    string? Report,
    string? Summary,
    string? PromptPath,
    string? ResponsePath);
