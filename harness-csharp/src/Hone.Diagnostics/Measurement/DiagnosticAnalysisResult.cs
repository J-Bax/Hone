using Hone.Core.Models;

namespace Hone.Diagnostics.Measurement;

/// <summary>
/// Result of running all enabled analyzers against merged collector data.
/// </summary>
public sealed record DiagnosticAnalysisResult(
    bool Success,
    IReadOnlyDictionary<string, AnalyzerResult> Reports);
