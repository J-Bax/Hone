namespace Hone.SourceControl.Experiments;

/// <summary>
/// Result of applying a suggestion to an experiment branch.
/// </summary>
/// <param name="Success">Whether the operation completed without errors.</param>
/// <param name="BranchName">Name of the created experiment branch, or <c>null</c> on failure.</param>
/// <param name="ErrorMessage">Error details when <paramref name="Success"/> is <c>false</c>.</param>
public sealed record ApplySuggestionResult(
    bool Success,
    string? BranchName,
    string? ErrorMessage);
