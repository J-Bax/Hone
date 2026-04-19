namespace Hone.Core.Contracts;

/// <summary>
/// Extends version control implementations with path-filtered working tree checks.
/// </summary>
public interface IPathFilteringVersionControl
{
    /// <summary>
    /// Determines whether the working tree is clean after excluding managed paths.
    /// </summary>
    public Task<bool> IsWorkingTreeCleanAsync(
        string workingDir,
        IEnumerable<string> ignoredPaths,
        CancellationToken ct = default);
}
