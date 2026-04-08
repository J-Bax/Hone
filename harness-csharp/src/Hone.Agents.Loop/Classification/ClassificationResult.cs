using Hone.Core.Models;

namespace Hone.Agents.Loop.Classification;

/// <summary>
/// Result of the classification agent invocation.
/// </summary>
/// <remarks>
/// <para>When <see cref="Success"/> is <see langword="true"/>:</para>
/// <list type="bullet">
///   <item><see cref="Scope"/> is non-null (validated before construction).</item>
///   <item><see cref="Reasoning"/> may still be null if the agent omitted it.</item>
/// </list>
/// <para>When <see cref="Success"/> is <see langword="false"/> or the agent returned
/// an unparseable response, <see cref="Scope"/> may be null.</para>
/// </remarks>
public sealed record ClassificationResult(
    bool Success,
    OpportunityScope? Scope,
    string? Reasoning,
    string Response);
