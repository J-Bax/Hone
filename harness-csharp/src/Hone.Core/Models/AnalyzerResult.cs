namespace Hone.Core.Models;

/// <summary>
/// Result produced by an analyzer plugin.
/// </summary>
public sealed record AnalyzerResult(
    bool Success,
    object? Report = null,
    string? Summary = null,
    string? PromptPath = null,
    string? ResponsePath = null,
    string? Error = null);
