namespace Hone.Core.Models;

/// <summary>
/// Per-metric detail within a comparison result.
/// </summary>
public sealed record MetricComparison(
    string MetricName,
    double Current,
    double Previous,
    double? Baseline,
    double DeltaPct,
    double AbsoluteDelta,
    bool Improved,
    bool Regressed);
