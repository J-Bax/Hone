namespace Hone.Core.Models;

/// <summary>
/// Outcome of a performance experiment run.
/// </summary>
public enum ExperimentOutcome
{
    /// <summary>The target metric improved.</summary>
    Improved,

    /// <summary>The target metric regressed.</summary>
    Regressed,

    /// <summary>No statistically significant change was detected.</summary>
    Stale,

    /// <summary>An efficiency improvement was detected.</summary>
    EfficiencyWin,
}
