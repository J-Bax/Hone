using Hone.Core.Models;

namespace Hone.Agents.Loop.Analysis;

/// <summary>
/// Result of the analysis agent invocation.
/// </summary>
/// <remarks>
/// <para>When <see cref="Success"/> is <see langword="true"/>:</para>
/// <list type="bullet">
///   <item><see cref="FilePath"/> is non-null (the primary opportunity's target file).</item>
///   <item><see cref="Explanation"/> is non-null.</item>
///   <item><see cref="Opportunities"/> contains at least one validated entry.</item>
/// </list>
/// <para>When <see cref="Success"/> is <see langword="false"/>:</para>
/// <list type="bullet">
///   <item><see cref="FilePath"/> is null.</item>
///   <item><see cref="Explanation"/> is null.</item>
///   <item><see cref="Opportunities"/> is an empty list.</item>
/// </list>
/// </remarks>
public sealed record AnalysisResult(
    bool Success,
    string? FilePath,
    string? Explanation,
    IReadOnlyList<Opportunity> Opportunities,
    string Prompt,
    string Response);
