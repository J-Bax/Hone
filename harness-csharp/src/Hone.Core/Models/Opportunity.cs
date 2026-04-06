using System.Text.Json.Serialization;

namespace Hone.Core.Models;

/// <summary>
/// An optimization opportunity identified by the analyst.
/// </summary>
public sealed record Opportunity(
    string FilePath,
    string Title,
    string Explanation,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] OpportunityScope Scope,
    string? RootCause,
    string? ImpactEstimate);
