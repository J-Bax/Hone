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

    /// <summary>The implementer could not produce a viable change.</summary>
    ImplementationFailed = 5,

    /// <summary>The experiment failed during the build step.</summary>
    BuildFailed = 6,

    /// <summary>The experiment failed during functional verification.</summary>
    TestFailed = 7,

    /// <summary>The experiment failed while starting the target for verification.</summary>
    StartFailed = 8,

    /// <summary>The experiment failed during evaluation measurement.</summary>
    LoadTestFailed = 9,
}
