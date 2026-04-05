namespace Hone.Reporting.PullRequest;

/// <summary>
/// The type of experiment PR body to generate.
/// </summary>
internal enum PrBodyType
{
    /// <summary>The experiment was accepted (improvement detected).</summary>
    Accepted,

    /// <summary>The experiment was rejected (regression, stale, build/test failure, etc.).</summary>
    Rejected,
}
