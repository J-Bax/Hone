using Hone.Core.Contracts;

namespace Hone.SourceControl.Experiments;

/// <summary>
/// Manages experiment branches — creating branches with applied suggestions and
/// reverting experiments back to their original state.
/// Replaces <c>Apply-Suggestion.ps1</c> and <c>Revert-ExperimentCode.ps1</c>.
/// </summary>
public sealed class ExperimentBranchManager(IVersionControl versionControl)
{
    /// <summary>
    /// Creates an experiment branch, writes the suggestion content, and commits.
    /// </summary>
    /// <param name="options">Options describing the suggestion to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result indicating success or failure with the created branch name.</returns>
    public async Task<ApplySuggestionResult> ApplySuggestionAsync(
        ApplySuggestionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            ThrowIfPathOutsideRoot(options.TargetFilePath, options.WorkingDir);

            string branchName = $"{options.BranchPrefix}-{options.Experiment}";
            string commitMessage = $"hone(experiment-{options.Experiment}): {options.Description}";

            // 1. Ensure we're on the base branch (clean starting point)
            // TODO(phase-7+): Preserve runtime state (results directory) across branch
            // switches — matches Get-PreservedRuntimeState / Restore-PreservedRuntimeState
            // in Apply-Suggestion.ps1.
            await versionControl.CheckoutAsync(
                options.WorkingDir, options.BaseBranch, create: false, ct).ConfigureAwait(false);

            // 2. Create the experiment branch from the base
            await versionControl.CheckoutAsync(
                options.WorkingDir, branchName, create: true, ct).ConfigureAwait(false);

            // 3. Write the suggestion content to the target file
            await File.WriteAllTextAsync(options.TargetFilePath, options.SuggestionContent, ct).ConfigureAwait(false);

            // 4. Commit the change
            // TODO(phase-7+): Stage experiment artifacts (analysis, iterations, metrics)
            // before committing — matches Stage-ExperimentArtifacts.ps1 behavior.
            await versionControl.CommitAsync(
                options.WorkingDir, commitMessage, [options.TargetFilePath], ct).ConfigureAwait(false);

            return new ApplySuggestionResult(Success: true, BranchName: branchName, ErrorMessage: null);
        }
#pragma warning disable CA1031 // Catch general exception — PS parity: scripts return error objects instead of throwing
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new ApplySuggestionResult(Success: false, BranchName: null, ErrorMessage: ex.Message);
        }
    }

    /// <summary>
    /// Reverts an experiment by soft-resetting the last commit, restoring the original
    /// file content, and committing a revert record.
    /// </summary>
    /// <param name="options">Options describing the experiment to revert.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result indicating success or failure.</returns>
    public async Task<RevertExperimentResult> RevertExperimentAsync(
        RevertExperimentOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            ThrowIfPathOutsideRoot(options.TargetFilePath, options.WorkingDir);

            // 1. Verify we're on the expected experiment branch
            string currentBranch = await versionControl.GetCurrentBranchAsync(
                options.WorkingDir, ct).ConfigureAwait(false);

            if (!string.Equals(currentBranch, options.BranchName, StringComparison.Ordinal))
            {
                return new RevertExperimentResult(
                    Success: false,
                    ErrorMessage: $"Expected branch '{options.BranchName}' but currently on '{currentBranch}'.");
            }

            string revertMessage = $"hone(experiment-{options.Experiment}): revert — {options.Outcome}";

            // 2. Soft-reset the last commit (unstages the experiment change)
            await versionControl.RevertLastCommitAsync(
                options.WorkingDir, ct).ConfigureAwait(false);

            // 3. Write the original file content back
            await File.WriteAllTextAsync(options.TargetFilePath, options.OriginalContent, ct).ConfigureAwait(false);

            // 4. Commit the revert
            await versionControl.CommitAsync(
                options.WorkingDir, revertMessage, [options.TargetFilePath], ct).ConfigureAwait(false);

            return new RevertExperimentResult(Success: true, ErrorMessage: null);
        }
#pragma warning disable CA1031 // Catch general exception — PS parity: scripts return error objects instead of throwing
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new RevertExperimentResult(Success: false, ErrorMessage: ex.Message);
        }
    }

    /// <summary>
    /// Guards against path traversal by verifying <paramref name="filePath"/> resolves
    /// within <paramref name="rootDir"/>.  Reproduces the protection from
    /// <c>Apply-Suggestion.ps1</c>.
    /// </summary>
    private static void ThrowIfPathOutsideRoot(string filePath, string rootDir)
    {
        string resolvedPath = Path.GetFullPath(filePath);
        string allowedRoot = Path.GetFullPath(rootDir);

        // Ensure the root ends with a directory separator so that
        // "C:\repo-other" doesn't match "C:\repo".
        if (!allowedRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            allowedRoot += Path.DirectorySeparatorChar;
        }

        if (!resolvedPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Path traversal blocked: '{filePath}' resolves outside '{rootDir}'.",
                nameof(filePath));
        }
    }
}
