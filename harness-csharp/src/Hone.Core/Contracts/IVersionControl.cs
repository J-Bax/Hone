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
    /// Reverts the last commit in the specified working directory.
    /// </summary>
    public Task RevertLastCommitAsync(string workingDir, CancellationToken ct = default);
}
