using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Orchestration.Queue;
using Hone.Orchestration.State;

namespace Hone.Orchestration.Failure;

/// <summary>
/// Unified handler for experiment failures in stacked-diffs mode.
/// Performs the standard rejection sequence: clean experiment state, optionally record
/// metadata via a caller-supplied callback, and optionally mark the queue
/// item done.
/// </summary>
/// <remarks>
/// Metadata recording is
/// delegated to the caller (the loop runner owns <c>run-metadata.json</c>)
/// through an optional <see cref="Func{T, TResult}"/> callback.
/// </remarks>
internal sealed class ExperimentFailureHandler
{
    private readonly IVersionControl _versionControl;
    private readonly IRunStateStore _runStateStore;
    private readonly OptimizationQueueManager _queueManager;
    private readonly IHoneEventSink _eventSink;

    internal ExperimentFailureHandler(
        IVersionControl versionControl,
        IRunStateStore runStateStore,
        OptimizationQueueManager queueManager,
        IHoneEventSink eventSink)
    {
        _versionControl = versionControl;
        _runStateStore = runStateStore;
        _queueManager = queueManager;
        _eventSink = eventSink;
    }

    /// <summary>
    /// Executes the failure-handling sequence: cleanup → metadata → queue mark.
    /// </summary>
    /// <param name="context">Details about the failed experiment.</param>
    /// <param name="onMetadataUpdate">
    /// Optional callback invoked when metadata should be recorded.
    /// The loop runner typically supplies this to write to <c>run-metadata.json</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result summarising which steps succeeded.</returns>
    internal async Task<FailureHandlerResult> HandleFailureAsync(
        FailureContext context,
        Func<FailureContext, Task>? onMetadataUpdate = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        CleanupExecutionResult cleanupResult = await CleanupExperimentAsync(context, ct).ConfigureAwait(false);
        bool metadataUpdated = await UpdateMetadataAsync(context, onMetadataUpdate).ConfigureAwait(false);
        bool queueMarked = MarkQueueDone(context);

        _eventSink.Emit(new StatusMessage(
            $"Failure handler completed for experiment {context.Experiment}: " +
            $"cleanup={cleanupResult.CleanupSucceeded}, verified={cleanupResult.VerificationSucceeded}, " +
            $"metadata={metadataUpdated}, queue={queueMarked}",
            LogLevel.Info,
            DateTimeOffset.UtcNow,
            context.Experiment));

        return new FailureHandlerResult(
            Success: cleanupResult.Success,
            CleanupSucceeded: cleanupResult.CleanupSucceeded,
            VerificationSucceeded: cleanupResult.VerificationSucceeded,
            MetadataUpdated: metadataUpdated,
            QueueMarked: queueMarked,
            CleanupManifestPath: cleanupResult.ManifestPath,
            TrackedPaths: cleanupResult.TrackedPaths,
            UntrackedPaths: cleanupResult.UntrackedPaths,
            ExpectedStableHeadSha: context.ExpectedStableHeadSha,
            ObservedHeadSha: cleanupResult.ObservedHeadSha,
            ObservedBranch: cleanupResult.ObservedBranch,
            WorktreeCleanAfterCleanup: cleanupResult.WorktreeCleanAfterCleanup,
            FailureMessage: cleanupResult.FailureMessage);
    }

    // ── Step 1: Cleanup ─────────────────────────────────────────────────────

    private async Task<CleanupExecutionResult> CleanupExperimentAsync(
        FailureContext context,
        CancellationToken ct)
    {
        string manifestPath = string.IsNullOrWhiteSpace(context.CleanupManifestPath)
            ? _runStateStore.GetCleanupManifestPath(context.Experiment)
            : context.CleanupManifestPath;
        CleanupManifest? manifest = null;

        try
        {
            manifest = await LoadOrCreateCleanupManifestAsync(context, manifestPath, ct).ConfigureAwait(false);

            await _versionControl.RestoreTrackedPathsAsync(
                context.TargetDir,
                context.BaseBranch,
                manifest.TrackedPaths,
                ct).ConfigureAwait(false);

            IReadOnlyList<string> pathsToRemove = FilterExistingPaths(context.TargetDir, manifest.UntrackedPaths);
            await _versionControl.RemoveUntrackedPathsAsync(context.TargetDir, pathsToRemove, ct).ConfigureAwait(false);

            await _versionControl.CheckoutAsync(
                context.TargetDir,
                context.BaseBranch,
                create: false,
                ct).ConfigureAwait(false);

            string observedBranch = await _versionControl.GetCurrentBranchAsync(context.TargetDir, ct)
                .ConfigureAwait(false);
            string observedHeadSha = await _versionControl.GetHeadShaAsync(context.TargetDir, ct)
                .ConfigureAwait(false);
            bool worktreeClean = await IsManagedWorkingTreeCleanAsync(context, ct).ConfigureAwait(false);

            bool branchMatches = string.Equals(observedBranch, context.BaseBranch, StringComparison.Ordinal);
            bool stableHeadMatches = string.IsNullOrWhiteSpace(context.ExpectedStableHeadSha)
                || string.Equals(
                    observedHeadSha,
                    context.ExpectedStableHeadSha,
                    StringComparison.OrdinalIgnoreCase);
            bool verificationSucceeded = branchMatches && stableHeadMatches && worktreeClean;

            string? failureMessage = verificationSucceeded
                ? null
                : BuildVerificationFailureMessage(
                    context,
                    observedBranch,
                    observedHeadSha,
                    branchMatches,
                    stableHeadMatches,
                    worktreeClean);

            if (!verificationSucceeded)
            {
                _eventSink.Emit(new StatusMessage(
                    failureMessage!,
                    LogLevel.Warning,
                    DateTimeOffset.UtcNow,
                    context.Experiment));
            }

            return new CleanupExecutionResult(
                Success: verificationSucceeded,
                CleanupSucceeded: true,
                VerificationSucceeded: verificationSucceeded,
                ManifestPath: manifestPath,
                TrackedPaths: manifest.TrackedPaths,
                UntrackedPaths: manifest.UntrackedPaths,
                ObservedHeadSha: observedHeadSha,
                ObservedBranch: observedBranch,
                WorktreeCleanAfterCleanup: worktreeClean,
                FailureMessage: failureMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string message = $"Cleanup failed for experiment {context.Experiment}: {ex.Message}";
            _eventSink.Emit(new StatusMessage(
                message,
                LogLevel.Warning,
                DateTimeOffset.UtcNow,
                context.Experiment));
            return new CleanupExecutionResult(
                Success: false,
                CleanupSucceeded: false,
                VerificationSucceeded: false,
                ManifestPath: manifestPath,
                TrackedPaths: manifest?.TrackedPaths ?? [],
                UntrackedPaths: manifest?.UntrackedPaths ?? [],
                ObservedHeadSha: null,
                ObservedBranch: null,
                WorktreeCleanAfterCleanup: false,
                FailureMessage: message);
        }
    }

    private async Task<bool> IsManagedWorkingTreeCleanAsync(FailureContext context, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(context.ResultsPath) &&
            _versionControl is IPathFilteringVersionControl filteringVersionControl)
        {
            string managedResultsPath = Path.IsPathRooted(context.ResultsPath)
                ? Path.GetRelativePath(context.TargetDir, context.ResultsPath)
                : context.ResultsPath;

            return await filteringVersionControl.IsWorkingTreeCleanAsync(
                context.TargetDir,
                [managedResultsPath],
                ct).ConfigureAwait(false);
        }

        return await _versionControl.IsWorkingTreeCleanAsync(context.TargetDir, ct).ConfigureAwait(false);
    }

    // ── Step 2: Metadata ────────────────────────────────────────────────────

    private static async Task<bool> UpdateMetadataAsync(
        FailureContext context,
        Func<FailureContext, Task>? onMetadataUpdate)
    {
        if (context.SkipMetadataUpdate || onMetadataUpdate is null)
        {
            return false;
        }

        await onMetadataUpdate(context).ConfigureAwait(false);
        return true;
    }

    // ── Step 3: Queue ───────────────────────────────────────────────────────

    private bool MarkQueueDone(FailureContext context)
    {
        if (context.SkipQueueMarkDone || string.IsNullOrEmpty(context.QueueItemId))
        {
            return false;
        }

        _queueManager.MarkDone(context.QueueItemId, context.Outcome, context.Experiment);
        return true;
    }

    private async Task<CleanupManifest> LoadOrCreateCleanupManifestAsync(
        FailureContext context,
        string manifestPath,
        CancellationToken ct)
    {
        CleanupManifest? existingManifest = await _runStateStore.LoadCleanupManifestAsync(manifestPath, ct)
            .ConfigureAwait(false);
        if (existingManifest is not null)
        {
            ValidateManifest(existingManifest, context, manifestPath);
            return existingManifest with
            {
                TrackedPaths = NormalizePaths(existingManifest.TrackedPaths),
                UntrackedPaths = NormalizePaths(existingManifest.UntrackedPaths),
            };
        }

        string candidateHeadSha = await _versionControl.GetHeadShaAsync(context.TargetDir, ct)
            .ConfigureAwait(false);
        IReadOnlyList<string> trackedPaths = NormalizePaths(
            await _versionControl.GetTouchedTrackedPathsAsync(context.TargetDir, context.BaseBranch, ct)
                .ConfigureAwait(false));
        IReadOnlyList<string> untrackedPaths = NormalizePaths(
            (context.KnownUntrackedPaths ?? [])
                .Concat(await _versionControl.GetUntrackedPathsAsync(context.TargetDir, ct).ConfigureAwait(false)));

        var manifest = new CleanupManifest
        {
            Experiment = context.Experiment,
            BranchName = context.BranchName,
            BaseBranch = context.BaseBranch,
            CandidateHeadSha = candidateHeadSha,
            ExpectedStableHeadSha = context.ExpectedStableHeadSha,
            TrackedPaths = trackedPaths,
            UntrackedPaths = untrackedPaths,
        };

        await _runStateStore.SaveCleanupManifestAsync(manifestPath, manifest, ct).ConfigureAwait(false);
        return manifest;
    }

    private static void ValidateManifest(
        CleanupManifest manifest,
        FailureContext context,
        string manifestPath)
    {
        if (manifest.Experiment != context.Experiment)
        {
            throw new InvalidOperationException(
                $"Cleanup manifest '{manifestPath}' belongs to experiment {manifest.Experiment}, not {context.Experiment}.");
        }

        if (!string.Equals(manifest.BranchName, context.BranchName, StringComparison.Ordinal) ||
            !string.Equals(manifest.BaseBranch, context.BaseBranch, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cleanup manifest '{manifestPath}' does not match branch '{context.BranchName}' on base '{context.BaseBranch}'.");
        }

        if (!string.IsNullOrWhiteSpace(context.ExpectedStableHeadSha) &&
            !string.IsNullOrWhiteSpace(manifest.ExpectedStableHeadSha) &&
            !string.Equals(
                manifest.ExpectedStableHeadSha,
                context.ExpectedStableHeadSha,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Cleanup manifest '{manifestPath}' expects stable head '{manifest.ExpectedStableHeadSha}', not '{context.ExpectedStableHeadSha}'.");
        }
    }

    private static string[] NormalizePaths(IEnumerable<string> paths) =>
    [
        .. paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeGitPath)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal),
    ];

    private static string[] FilterExistingPaths(string targetDir, IEnumerable<string> paths) =>
        NormalizePaths(paths.Where(path => PathExists(targetDir, path)));

    private static bool PathExists(string targetDir, string relativePath)
    {
        string fullPath = ResolvePathWithinTarget(targetDir, relativePath);
        return File.Exists(fullPath) || Directory.Exists(fullPath);
    }

    private static string ResolvePathWithinTarget(string targetDir, string relativePath)
    {
        string normalizedTargetDir = Path.GetFullPath(targetDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Combine(
            normalizedTargetDir,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!string.Equals(fullPath, normalizedTargetDir, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(
                normalizedTargetDir + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Cleanup path '{relativePath}' resolves outside target directory '{normalizedTargetDir}'.");
        }

        return fullPath;
    }

    private static string NormalizeGitPath(string path) =>
        path.Replace('\\', '/');

    private static string BuildVerificationFailureMessage(
        FailureContext context,
        string observedBranch,
        string observedHeadSha,
        bool branchMatches,
        bool stableHeadMatches,
        bool worktreeClean)
    {
        var failures = new List<string>();
        if (!branchMatches)
        {
            failures.Add(
                $"branch '{observedBranch}' does not match expected stable branch '{context.BaseBranch}'");
        }

        if (!stableHeadMatches)
        {
            failures.Add(
                $"HEAD '{observedHeadSha}' does not match expected stable SHA '{context.ExpectedStableHeadSha}'");
        }

        if (!worktreeClean)
        {
            failures.Add($"stable branch '{context.BaseBranch}' is still dirty");
        }

        return $"Cleanup verification failed for experiment {context.Experiment}: {string.Join("; ", failures)}.";
    }

    private sealed record CleanupExecutionResult(
        bool Success,
        bool CleanupSucceeded,
        bool VerificationSucceeded,
        string ManifestPath,
        IReadOnlyList<string> TrackedPaths,
        IReadOnlyList<string> UntrackedPaths,
        string? ObservedHeadSha,
        string? ObservedBranch,
        bool WorktreeCleanAfterCleanup,
        string? FailureMessage);
}
