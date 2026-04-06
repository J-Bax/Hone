using System.Text.Json.Serialization;

namespace Hone.Core.Models;

/// <summary>
/// An entry in the optimization work queue.
/// </summary>
public sealed record QueueItem(
    string Id,
    string FilePath,
    string Explanation,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] OpportunityScope Scope,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] QueueItemStatus Status,
    int? TriedByExperiment,
    string? Outcome);
