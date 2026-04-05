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
    public IReadOnlyList<MetricComparison> Details { get; init; } = Details;
}
