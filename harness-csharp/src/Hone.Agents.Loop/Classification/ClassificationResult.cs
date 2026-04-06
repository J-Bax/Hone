using Hone.Core.Models;

namespace Hone.Agents.Loop.Classification;

/// <summary>
/// Result of the classification agent invocation.
/// </summary>
public sealed record ClassificationResult(
    bool Success,
    OpportunityScope Scope,
    string? Reasoning,
    string Response);
