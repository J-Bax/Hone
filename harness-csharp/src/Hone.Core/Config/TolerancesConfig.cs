namespace Hone.Core.Config;

/// <summary>
/// Performance tolerance thresholds for accept/reject decisions.
/// </summary>
public sealed record TolerancesConfig(
    double MaxRegressionPct = 0.10,
    double MinAbsoluteP95DeltaMs = 5,
    double MinAbsoluteRPSDelta = 5,
    double MinAbsoluteErrorRateDelta = 0.005,
    double MinImprovementPct = 0,
    int StaleExperimentsBeforeStop = 2,
    int MaxConsecutiveFailures = 10,
    EfficiencyConfig? Efficiency = null)
{
    /// <summary>
    /// Gets the efficiency tiebreaker settings.
    /// </summary>
    public EfficiencyConfig Efficiency { get; init; } = Efficiency ?? new EfficiencyConfig();
}
