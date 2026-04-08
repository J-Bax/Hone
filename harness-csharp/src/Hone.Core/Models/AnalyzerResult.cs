namespace Hone.Core.Models;

/// <summary>
/// Result produced by an analyzer plugin.
/// </summary>
/// <remarks>
/// All properties except <see cref="Success"/> are optional and default to
/// <see langword="null"/>. Analyzers populate only the fields relevant to
/// their output (e.g., <see cref="Report"/> for structured data,
/// <see cref="Summary"/> for human-readable text, <see cref="Error"/> on failure).
/// </remarks>
public sealed record AnalyzerResult(
    bool Success,
    object? Report = null,
    string? Summary = null,
    string? PromptPath = null,
    string? ResponsePath = null,
    string? Error = null);
