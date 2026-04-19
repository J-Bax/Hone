using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.SourceControl.Git;

/// <summary>
/// Git-based implementation of <see cref="IVersionControl"/> that shells out
/// to the <c>git</c> CLI via <see cref="IProcessRunner"/>.
/// </summary>
public sealed class GitVersionControl(IProcessRunner processRunner) : IVersionControl, IPathFilteringVersionControl
{
    private static readonly string[] StatusPorcelainNoRenamesArgs =
        ["status", "--porcelain=v1", "-z", "--untracked-files=normal", "--no-renames"];

    /// <inheritdoc />
    public async Task<string> GetCurrentBranchAsync(string workingDir, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDir);

        ProcessResult result = await processRunner.RunAsync(
            "git",
            ["rev-parse", "--abbrev-ref", "HEAD"],
            workingDir,
            timeout: null,
            ct).ConfigureAwait(false);

        return result.Success
            ? result.Output.Trim()
            : throw new InvalidOperationException(
                $"Failed to get current branch: {result.Output}");
    }

    /// <inheritdoc />
    public async Task<string> GetHeadShaAsync(string workingDir, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDir);

        ProcessResult result = await processRunner.RunAsync(
            "git",
            ["rev-parse", "HEAD"],
            workingDir,
            timeout: null,
            ct).ConfigureAwait(false);

        return result.Success
            ? result.Output.Trim()
            : throw new InvalidOperationException(
                $"Failed to get HEAD SHA: {result.Output}");
    }

    /// <inheritdoc />
    public async Task<bool> LocalBranchExistsAsync(string workingDir, string branch, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDir);
        ArgumentException.ThrowIfNullOrEmpty(branch);

        ProcessResult result = await processRunner.RunAsync(
            "git",
            ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"],
            workingDir,
            timeout: null,
            ct).ConfigureAwait(false);

        return result.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw new InvalidOperationException(
                $"Failed to check local branch '{branch}': {result.Output}"),
        };
    }

    /// <inheritdoc />
    public async Task<bool> IsWorkingTreeCleanAsync(string workingDir, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDir);

        ProcessResult result = await processRunner.RunAsync(
            "git",
            ["status", "--porcelain", "--untracked-files=normal"],
            workingDir,
            timeout: null,
            ct).ConfigureAwait(false);

        return result.Success
            ? string.IsNullOrWhiteSpace(result.Output)
            : throw new InvalidOperationException(
                $"Failed to get working tree status: {result.Output}");
    }

    /// <inheritdoc cref="IPathFilteringVersionControl.IsWorkingTreeCleanAsync(string, IEnumerable{string}, CancellationToken)" />
    public async Task<bool> IsWorkingTreeCleanAsync(
        string workingDir,
        IEnumerable<string> ignoredPaths,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDir);
        ArgumentNullException.ThrowIfNull(ignoredPaths);

        ProcessResult result = await processRunner.RunAsync(
            "git",
            StatusPorcelainNoRenamesArgs,
            workingDir,
            timeout: null,
            ct).ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Failed to get working tree status: {result.Output}");
        }

        string[] ignored = NormalizeIgnoredPaths(ignoredPaths);
        string[] dirtyPaths = NormalizePaths(ParseStatusPaths(
            result.Output,
            includeTracked: true,
            includeUntracked: true));

        return dirtyPaths.All(path => IsIgnoredPath(path, ignored));
    }

    /// <inheritdoc />
    public async Task CheckoutAsync(string workingDir, string branch, bool create = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDir);
        ArgumentException.ThrowIfNullOrEmpty(branch);

        string[] arguments = create
            ? ["checkout", "-b", branch]
            : ["checkout", branch];

        ProcessResult result = await processRunner.RunAsync(
            "git",
            arguments,
            workingDir,
            timeout: null,
            ct).ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Failed to checkout branch '{branch}': {result.Output}");
        }
    }

    /// <inheritdoc />
    public async Task CommitAsync(string workingDir, string message, IEnumerable<string>? paths = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDir);
        ArgumentException.ThrowIfNullOrEmpty(message);

        if (paths is not null)
        {
            List<string> addArgs = ["add", "--"];
            addArgs.AddRange(paths);

            ProcessResult addResult = await processRunner.RunAsync(
                "git",
                addArgs,
                workingDir,
                timeout: null,
                ct).ConfigureAwait(false);

            if (!addResult.Success)
            {
                throw new InvalidOperationException(
                    $"Failed to stage files: {addResult.Output}");
            }
        }

        ProcessResult commitResult = await processRunner.RunAsync(
            "git",
            ["commit", "--no-gpg-sign", "-m", message],
            workingDir,
            timeout: null,
            ct).ConfigureAwait(false);

        if (!commitResult.Success)
        {
            throw new InvalidOperationException(
                $"Failed to commit: {commitResult.Output}");
        }
    }

    /// <inheritdoc />
    public async Task<string> GetDiffAsync(string workingDir, string? baseBranch = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDir);

        string[] arguments = baseBranch is not null
            ? ["diff", "--", $"{baseBranch}...HEAD"]
            : ["diff"];

        ProcessResult result = await processRunner.RunAsync(
            "git",
            arguments,
            workingDir,
            timeout: null,
            ct).ConfigureAwait(false);

        return result.Success
            ? result.Output
            : throw new InvalidOperationException(
                $"Failed to get diff: {result.Output}");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetTouchedTrackedPathsAsync(
        string workingDir,
        string baseBranch,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDir);
        ArgumentException.ThrowIfNullOrEmpty(baseBranch);

        ProcessResult diffResult = await processRunner.RunAsync(
            "git",
            ["diff", "--name-only", "-z", "--no-renames", "--diff-filter=ACDMRTUXB", $"{baseBranch}...HEAD", "--"],
            workingDir,
            timeout: null,
            ct).ConfigureAwait(false);
        if (!diffResult.Success)
        {
            throw new InvalidOperationException(
                $"Failed to derive tracked paths from '{baseBranch}': {diffResult.Output}");
        }

        ProcessResult statusResult = await processRunner.RunAsync(
            "git",
            StatusPorcelainNoRenamesArgs,
            workingDir,
            timeout: null,
            ct).ConfigureAwait(false);
        if (!statusResult.Success)
        {
            throw new InvalidOperationException(
                $"Failed to derive tracked paths from working tree status: {statusResult.Output}");
        }

        return NormalizePaths(
            ParseNullSeparatedPaths(diffResult.Output)
                .Concat(ParseStatusPaths(statusResult.Output, includeTracked: true, includeUntracked: false)));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetUntrackedPathsAsync(
        string workingDir,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDir);

        ProcessResult result = await processRunner.RunAsync(
            "git",
            StatusPorcelainNoRenamesArgs,
            workingDir,
            timeout: null,
            ct).ConfigureAwait(false);

        return result.Success
            ? NormalizePaths(ParseStatusPaths(result.Output, includeTracked: false, includeUntracked: true))
            : throw new InvalidOperationException(
                $"Failed to derive untracked paths: {result.Output}");
    }

    /// <inheritdoc />
    public async Task RestoreTrackedPathsAsync(
        string workingDir,
        string sourceBranch,
        IEnumerable<string> paths,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDir);
        ArgumentException.ThrowIfNullOrEmpty(sourceBranch);
        ArgumentNullException.ThrowIfNull(paths);

        List<string> trackedPaths = [.. paths];
        if (trackedPaths.Count == 0)
        {
            return;
        }

        List<string> arguments = ["restore", "--source", sourceBranch, "--staged", "--worktree", "--"];
        arguments.AddRange(trackedPaths);

        ProcessResult result = await processRunner.RunAsync(
            "git",
            arguments,
            workingDir,
            timeout: null,
            ct).ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Failed to restore tracked paths from '{sourceBranch}': {result.Output}");
        }
    }

    /// <inheritdoc />
    public async Task RemoveUntrackedPathsAsync(
        string workingDir,
        IEnumerable<string> paths,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDir);
        ArgumentNullException.ThrowIfNull(paths);

        List<string> untrackedPaths = [.. paths];
        if (untrackedPaths.Count == 0)
        {
            return;
        }

        List<string> arguments = ["clean", "-f", "-d", "-x", "--"];
        arguments.AddRange(untrackedPaths);

        ProcessResult result = await processRunner.RunAsync(
            "git",
            arguments,
            workingDir,
            timeout: null,
            ct).ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Failed to remove untracked paths: {result.Output}");
        }
    }

    /// <inheritdoc />
    public async Task RevertLastCommitAsync(string workingDir, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDir);

        ProcessResult result = await processRunner.RunAsync(
            "git",
            ["reset", "--soft", "HEAD~1"],
            workingDir,
            timeout: null,
            ct).ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Failed to revert last commit: {result.Output}");
        }
    }

    private static string[] NormalizePaths(IEnumerable<string> paths) =>
    [
        .. paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal),
    ];

    private static string[] ParseNullSeparatedPaths(string output) =>
        output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static string[] NormalizeIgnoredPaths(IEnumerable<string> paths) =>
    [
        .. paths
            .Select(NormalizePathForComparison)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal),
    ];

    private static string NormalizePathForComparison(string path)
    {
        string normalized = path.Replace('\\', '/').Trim();

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.Trim('/');
    }

    private static bool IsIgnoredPath(string path, IReadOnlyCollection<string> ignoredPaths)
    {
        string normalizedPath = NormalizePathForComparison(path);

        foreach (string ignoredPath in ignoredPaths)
        {
            if (string.Equals(normalizedPath, ignoredPath, StringComparison.Ordinal) ||
                normalizedPath.StartsWith($"{ignoredPath}/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> ParseStatusPaths(
        string output,
        bool includeTracked,
        bool includeUntracked)
    {
        foreach (string entry in ParseNullSeparatedPaths(output))
        {
            if (entry.Length < 4)
            {
                continue;
            }

            string status = entry[..2];
            string path = entry[3..];
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (includeUntracked && string.Equals(status, "??", StringComparison.Ordinal))
            {
                yield return path;
                continue;
            }

            if (includeTracked &&
                !string.Equals(status, "??", StringComparison.Ordinal) &&
                !string.Equals(status, "!!", StringComparison.Ordinal))
            {
                yield return path;
            }
        }
    }
}
