namespace Hone.Core.Contracts;

/// <summary>
/// Generic code hosting abstraction (e.g., GitHub, Azure DevOps).
/// </summary>
public interface ICodeHost
{
    /// <summary>
    /// Pushes a branch to the remote code host.
    /// </summary>
    public Task<PushResult> PushBranchAsync(string workingDir, string branch, CancellationToken ct = default);

    /// <summary>
    /// Creates a pull request on the code host.
    /// </summary>
    public Task<PullRequestResult> CreatePullRequestAsync(CreatePrOptions options, CancellationToken ct = default);

    /// <summary>
    /// Gets the status of an existing pull request.
    /// </summary>
    public Task<PullRequestStatus> GetPullRequestStatusAsync(int prNumber, CancellationToken ct = default);
}
