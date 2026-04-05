using System.Text.Json.Serialization;

namespace Hone.Core.Models;

/// <summary>
/// Outcome of comparing metric sets between experiment runs.
/// </summary>
public sealed record ComparisonResult(
    bool Accepted,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ExperimentOutcome Outcome,
    double ImprovementPct,
    double RegressionPct,
    IReadOnlyList<MetricComparison> Details)
{
    /// <summary>
    /// Gets the per-metric comparison details, defaulting to an empty list.
    /// </summary>
    public IReadOnlyList<MetricComparison> Details { get; init; } = Details ?? [];
}
