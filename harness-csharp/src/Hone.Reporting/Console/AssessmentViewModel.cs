namespace Hone.Reporting.Console;

/// <summary>
/// View model for the compatibility assessment report.
/// </summary>
public sealed record AssessmentViewModel(
    string TargetName,
    string Overall,
    int Score,
    IReadOnlyList<AssessmentFindingViewModel> Blockers,
    IReadOnlyList<AssessmentFindingViewModel> Warnings,
    IReadOnlyList<AssessmentReadyViewModel> ReadyItems,
    string? OnboardingSummary);

/// <summary>
/// A single blocker or warning finding.
/// </summary>
public sealed record AssessmentFindingViewModel(string Area, string Issue, string Remediation);

/// <summary>
/// A single readiness item.
/// </summary>
public sealed record AssessmentReadyViewModel(string Area, string Detail);
