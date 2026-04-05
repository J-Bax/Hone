using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hone.Core.Models;

/// <summary>
/// Metadata for a single experiment within a run.
/// </summary>
public sealed record ExperimentMetadata(
    int Experiment,
    string StartedAt,
    string? CompletedAt,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ExperimentOutcome? Outcome,
    string? BranchName,
    string? BaseBranch,
    double? P95,
    double? RPS,
    int? PrNumber,
    Uri? PrUrl,
    int StaleCount,
    int ConsecutiveFailures)
{
    /// <summary>
    /// Gets or sets additional properties not explicitly modeled,
    /// allowing unknown JSON properties to round-trip through serialization.
    /// </summary>
    [JsonExtensionData]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0016:Prefer using collection abstraction instead of implementation", Justification = "JsonExtensionData requires Dictionary")]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}
