namespace Hone.Reporting.PullRequest;

/// <summary>
/// The type of experiment PR body to generate.
/// </summary>
internal enum PrBodyType
{
    /// <summary>Uninitialized or unknown body type.</summary>
    Unknown = 0,

    /// <summary>The experiment was accepted (improvement detected).</summary>
    Accepted = 1,

    /// <summary>The experiment was rejected (regression, stale, build/test failure, etc.).</summary>
    Rejected = 2,
}
