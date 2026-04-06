namespace Hone.Reporting.Rca;

/// <summary>
/// Estimated production impact of an optimization opportunity.
/// </summary>
internal sealed record ImpactEstimate(
    double TrafficPct,
    double LatencyReductionMs,
    double OverallP95ImprovementPct,
    string Confidence,
    string? Reasoning);
