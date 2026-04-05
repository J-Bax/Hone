namespace Hone.Core.Contracts;

/// <summary>
/// Result of creating a pull request on a code host.
/// </summary>
public sealed record PullRequestResult(
    bool Success,
    int? PrNumber,
    Uri? PrUrl);
