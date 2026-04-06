namespace Hone.SourceControl.Experiments;

/// <summary>
/// Result of reverting an experiment.
/// </summary>
/// <param name="Success">Whether the revert completed without errors.</param>
/// <param name="ErrorMessage">Error details when <paramref name="Success"/> is <c>false</c>.</param>
public sealed record RevertExperimentResult(
    bool Success,
    string? ErrorMessage);
