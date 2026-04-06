namespace Hone.Core.Models;

/// <summary>
/// Outcome of a performance experiment run.
/// </summary>
public enum ExperimentOutcome
{
    /// <summary>Uninitialized or unknown outcome.</summary>
    Unknown = 0,

    /// <summary>The target metric improved.</summary>
    Improved = 1,

    /// <summary>The target metric regressed.</summary>
    Regressed = 2,

    /// <summary>No statistically significant change was detected.</summary>
    Stale = 3,

    /// <summary>An efficiency improvement was detected.</summary>
    EfficiencyWin = 4,
}
