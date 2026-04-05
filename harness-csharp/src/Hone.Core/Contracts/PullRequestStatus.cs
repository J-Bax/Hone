namespace Hone.Core.Contracts;

/// <summary>
/// Status information for an existing pull request.
/// </summary>
public sealed record PullRequestStatus(
    int PrNumber,
    string State,
    bool Merged);
