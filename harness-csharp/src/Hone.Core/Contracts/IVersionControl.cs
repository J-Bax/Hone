namespace Hone.Core.Contracts;

/// <summary>
/// Generic version control system abstraction.
/// </summary>
public interface IVersionControl
{
    /// <summary>
    /// Gets the name of the currently checked-out branch.
    /// </summary>
    public Task<string> GetCurrentBranchAsync(string workingDir, CancellationToken ct = default);

    /// <summary>
    /// Gets the full SHA of the current <c>HEAD</c> commit.
    /// </summary>
    public Task<string> GetHeadShaAsync(string workingDir, CancellationToken ct = default);

    /// <summary>
    /// Determines whether the specified local branch exists.
    /// </summary>
    public Task<bool> LocalBranchExistsAsync(string workingDir, string branch, CancellationToken ct = default);

    /// <summary>
    /// Determines whether the working tree is clean.
    /// </summary>
    public Task<bool> IsWorkingTreeCleanAsync(string workingDir, CancellationToken ct = default);

    /// <summary>
    /// Checks out the specified branch, optionally creating it.
    /// </summary>
    /// <remarks>
    /// When <paramref name="create"/> is <see langword="true"/>, the new branch is created
    /// from the current HEAD.  The caller is responsible for positioning to the desired base
    /// branch before calling this method.  There is no start-point parameter because the
    /// abstraction intentionally avoids exposing VCS-specific branching semantics.
    /// </remarks>
    public Task CheckoutAsync(string workingDir, string branch, bool create = false, CancellationToken ct = default);

    /// <summary>
    /// Commits changes with the specified message, optionally limited to specific paths.
    /// </summary>
    public Task CommitAsync(string workingDir, string message, IEnumerable<string>? paths = null, CancellationToken ct = default);

    /// <summary>
    /// Gets the diff of changes, optionally against a base branch.
    /// </summary>
    public Task<string> GetDiffAsync(string workingDir, string? baseBranch = null, CancellationToken ct = default);

    /// <summary>
    /// Derives the tracked paths touched by the current experiment relative to the stable base branch.
    /// </summary>
    public Task<IReadOnlyList<string>> GetTouchedTrackedPathsAsync(
        string workingDir,
        string baseBranch,
        CancellationToken ct = default);

    /// <summary>
    /// Lists the current untracked paths in the working tree.
    /// </summary>
    public Task<IReadOnlyList<string>> GetUntrackedPathsAsync(
        string workingDir,
        CancellationToken ct = default);

    /// <summary>
    /// Restores the specified tracked paths from the given source branch.
    /// </summary>
    public Task RestoreTrackedPathsAsync(
        string workingDir,
        string sourceBranch,
        IEnumerable<string> paths,
        CancellationToken ct = default);

    /// <summary>
    /// Removes the specified untracked paths from the working tree.
    /// </summary>
    public Task RemoveUntrackedPathsAsync(
        string workingDir,
        IEnumerable<string> paths,
        CancellationToken ct = default);

    /// <summary>
    /// Reverts the last commit in the specified working directory.
    /// </summary>
    public Task RevertLastCommitAsync(string workingDir, CancellationToken ct = default);
}
