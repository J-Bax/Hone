using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.SourceControl.Git;

/// <summary>
/// Git-based implementation of <see cref="IVersionControl"/> that shells out
/// to the <c>git</c> CLI via <see cref="IProcessRunner"/>.
/// </summary>
public sealed class GitVersionControl(IProcessRunner processRunner) : IVersionControl
{
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

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Failed to get current branch: {result.Output}");
        }

        return result.Output.Trim();
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

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Failed to get diff: {result.Output}");
        }

        return result.Output;
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
}
