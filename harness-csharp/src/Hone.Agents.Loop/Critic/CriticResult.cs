namespace Hone.Agents.Loop.Critic;

/// <summary>
/// Result of the critic agent review.
/// </summary>
public sealed record CriticResult(
    bool Success,
    bool Approved,
    string? Verdict,
    string? Confidence,
    IReadOnlyList<CriticIssue>? Issues,
    string? Feedback,
    string? Summary,
    string Response);

/// <summary>
/// A single issue identified by the critic agent.
/// </summary>
public sealed record CriticIssue(
    string? Severity,
    string? Category,
    string? Description,
    string? Suggestion);
