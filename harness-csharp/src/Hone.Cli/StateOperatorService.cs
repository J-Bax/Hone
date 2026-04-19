using System.Text.Json;

using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Orchestration.Queue;
using Hone.Orchestration.State;
using Hone.SourceControl.Git;

namespace Hone.Cli;

internal sealed class StateOperatorService
{
    private static readonly JsonSerializerOptions RunMetadataJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _targetDir;
    private readonly string _defaultBranch;
    private readonly IVersionControl _versionControl;
    private readonly IRunStateStore _runStateStore;
    private readonly OptimizationQueueManager _queueManager;
    private readonly string _managedResultsPath;
    private readonly string _runMetadataPath;

    private StateOperatorService(
        string targetDir,
        string defaultBranch,
        string resultsPath,
        IVersionControl versionControl,
        IRunStateStore runStateStore,
        OptimizationQueueManager queueManager,
        string runMetadataPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetDir);
        ArgumentException.ThrowIfNullOrEmpty(defaultBranch);
        ArgumentException.ThrowIfNullOrEmpty(resultsPath);
        ArgumentNullException.ThrowIfNull(versionControl);
        ArgumentNullException.ThrowIfNull(runStateStore);
        ArgumentNullException.ThrowIfNull(queueManager);
        ArgumentException.ThrowIfNullOrEmpty(runMetadataPath);

        _targetDir = targetDir;
        _defaultBranch = defaultBranch;
        _managedResultsPath = Path.IsPathRooted(resultsPath)
            ? Path.GetRelativePath(targetDir, resultsPath)
            : resultsPath;
        _versionControl = versionControl;
        _runStateStore = runStateStore;
        _queueManager = queueManager;
        _runMetadataPath = runMetadataPath;
    }

    internal static StateOperatorService Create(string targetDir, HoneConfig config)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetDir);
        ArgumentNullException.ThrowIfNull(config);

        string metadataPath = Path.Combine(targetDir, config.Api.MetadataPath);
        string runMetadataPath = Path.Combine(targetDir, config.Api.ResultsPath, "run-metadata.json");

        return new StateOperatorService(
            targetDir,
            config.BaseBranch ?? "main",
            config.Api.ResultsPath,
            new GitVersionControl(new ProcessRunner()),
            new RunStateStore(targetDir, config.Api.MetadataPath),
            new OptimizationQueueManager(metadataPath, new HoneEventBus()),
            runMetadataPath);
    }

    internal async Task<StateInspectionResult> InspectAsync(CancellationToken ct = default)
    {
        var diagnostics = new List<StateDiagnostic>();
        StatePaths paths = new(
            _runStateStore.RunStatePath,
            Path.Combine(_runStateStore.MetadataDirectory, "experiment-queue.json"),
            _runMetadataPath);

        bool runStateExists = File.Exists(paths.RunStatePath);
        bool queueExists = File.Exists(paths.QueuePath);
        bool runMetadataExists = File.Exists(paths.RunMetadataPath);

        RunStateDocument? runState = null;
        string? runStateLoadError = null;
        try
        {
            runState = await _runStateStore.LoadAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            runStateLoadError = ex.Message;
            diagnostics.Add(new StateDiagnostic(StateDiagnosticSeverity.Error, ex.Message));
        }

        OptimizationQueue queue = new(GeneratedByExperiment: 0, Items: []);
        string? queueLoadError = null;
        try
        {
            queue = _queueManager.GetSnapshot();
        }
        catch (InvalidOperationException ex)
        {
            queueLoadError = ex.Message;
            diagnostics.Add(new StateDiagnostic(StateDiagnosticSeverity.Error, ex.Message));
        }

        RunMetadata? metadata = null;
        string? runMetadataLoadError = null;
        try
        {
            metadata = await LoadRunMetadataAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            runMetadataLoadError = ex.Message;
            diagnostics.Add(new StateDiagnostic(StateDiagnosticSeverity.Error, ex.Message));
        }

        GitObservation git = await ObserveGitAsync(
            runState?.StableBranch,
            runState?.CurrentExperiment?.BranchName,
            ct).ConfigureAwait(false);
        AddGitDiagnostics(git, diagnostics);

        var repairPlanBuilder = new StateRepairPlanBuilder();
        AnalyzeState(
            _defaultBranch,
            runStateExists,
            runState,
            runStateLoadError is null,
            queue,
            queueLoadError is null,
            metadata,
            runMetadataLoadError is null,
            git,
            diagnostics,
            repairPlanBuilder);

        return new StateInspectionResult(
            _targetDir,
            paths,
            runStateExists,
            queueExists,
            runMetadataExists,
            runState,
            queue,
            metadata,
            git,
            diagnostics,
            repairPlanBuilder.Build());
    }

    internal async Task<StateRepairResult> RepairAsync(CancellationToken ct = default)
    {
        StateInspectionResult before = await InspectAsync(ct).ConfigureAwait(false);
        StateInspectionResult current = before;
        var appliedSteps = new List<string>();
        var errors = new List<string>();

        for (int pass = 0; pass < 8 && current.RepairPlan.HasSteps; pass++)
        {
            StateRepairPlan plan = current.RepairPlan;

            foreach (StateRepairStep step in plan.Steps)
            {
                try
                {
                    await ApplyRepairStepAsync(step, ct).ConfigureAwait(false);
                    appliedSteps.Add(step.Description);
                }
                catch (InvalidOperationException ex)
                {
                    errors.Add($"{step.Description}: {ex.Message}");
                    StateInspectionResult failed = await InspectAsync(ct).ConfigureAwait(false);
                    return new StateRepairResult(before, appliedSteps, failed, errors);
                }
            }

            StateInspectionResult updated = await InspectAsync(ct).ConfigureAwait(false);
            current = updated;

            if (!updated.RepairPlan.HasSteps || HasSameRepairSteps(plan, updated.RepairPlan))
            {
                break;
            }
        }

        return new StateRepairResult(before, appliedSteps, current, errors);
    }

    private static void AnalyzeState(
        string defaultBranch,
        bool runStateExists,
        RunStateDocument? runState,
        bool runStateLoaded,
        OptimizationQueue queue,
        bool queueLoaded,
        RunMetadata? metadata,
        bool runMetadataLoaded,
        GitObservation git,
        List<StateDiagnostic> diagnostics,
        StateRepairPlanBuilder repairPlanBuilder)
    {
        List<QueueItem> inProgressItems = queueLoaded ? CollectInProgressItems(queue) : [];

        if (runStateExists && !runStateLoaded)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                "run-state.json is unreadable. Restore valid JSON before applying repairs."));
            return;
        }

        if (runState is null)
        {
            AnalyzeMissingRunState(
                defaultBranch,
                inProgressItems,
                metadata,
                runMetadataLoaded,
                git,
                diagnostics,
                repairPlanBuilder);
            return;
        }

        if (runState.Status == RecoveryState.RepairRequired)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Warning,
                "run-state.json is marked repair_required."));
        }

        RecoveryState effectiveStatus = DetermineEffectiveStatus(runState);
        CurrentExperimentState? currentExperiment = runState.CurrentExperiment;
        bool gitCoreAvailable = git.HasCoreState;

        if (string.IsNullOrWhiteSpace(runState.StableBranch))
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                "run-state.json is missing stableBranch."));
            return;
        }

        if (string.IsNullOrWhiteSpace(runState.StableHeadSha))
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"run-state.json is missing stableHeadSha for stable branch '{runState.StableBranch}'."));
            return;
        }

        if (git.StableBranchExists == false)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"Stable branch '{runState.StableBranch}' recorded in run-state.json does not exist locally."));
        }

        string? expectedStableBranch = runMetadataLoaded
            ? DetermineStableBranchFromMetadata(
                metadata,
                defaultBranch,
                ignoreExperiment: effectiveStatus == RecoveryState.Finalizing ? currentExperiment?.Number : null)
            : null;

        if (!string.IsNullOrWhiteSpace(expectedStableBranch) &&
            !string.Equals(expectedStableBranch, runState.StableBranch, StringComparison.Ordinal))
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"Run metadata expects stable branch '{expectedStableBranch}', but run-state.json records '{runState.StableBranch}'."));
        }

        switch (effectiveStatus)
        {
            case RecoveryState.Idle:
                AnalyzeIdleState(
                    runState,
                    inProgressItems,
                    gitCoreAvailable,
                    git,
                    diagnostics,
                    repairPlanBuilder);
                return;

            case RecoveryState.ExperimentLeased:
            case RecoveryState.BranchCreated:
                if (!TryValidateActiveState(
                        runState,
                        currentExperiment,
                        effectiveStatus,
                        queue,
                        queueLoaded,
                        inProgressItems,
                        metadata,
                        runMetadataLoaded,
                        diagnostics,
                        repairPlanBuilder,
                        out QueueItem? leasedQueueItem))
                {
                    return;
                }

                AnalyzePreCandidateState(
                    runState,
                    currentExperiment!,
                    leasedQueueItem!,
                    gitCoreAvailable,
                    git,
                    diagnostics,
                    repairPlanBuilder);
                return;

            case RecoveryState.CandidateCommitted:
                if (!TryValidateActiveState(
                        runState,
                        currentExperiment,
                        effectiveStatus,
                        queue,
                        queueLoaded,
                        inProgressItems,
                        metadata,
                        runMetadataLoaded,
                        diagnostics,
                        repairPlanBuilder,
                        out _))
                {
                    return;
                }

                AnalyzeCandidateCommittedState(
                    runState,
                    currentExperiment!,
                    gitCoreAvailable,
                    git,
                    diagnostics,
                    repairPlanBuilder);
                return;

            case RecoveryState.Finalizing:
                if (!TryValidateActiveState(
                        runState,
                        currentExperiment,
                        effectiveStatus,
                        queue,
                        queueLoaded,
                        inProgressItems,
                        metadata,
                        runMetadataLoaded,
                        diagnostics,
                        repairPlanBuilder,
                        out QueueItem? finalizingQueueItem))
                {
                    return;
                }

                AnalyzeFinalizingState(
                    runState,
                    currentExperiment!,
                    finalizingQueueItem!,
                    metadata,
                    runMetadataLoaded,
                    gitCoreAvailable,
                    git,
                    diagnostics,
                    repairPlanBuilder);
                return;

            case RecoveryState.RepairRequired:
                diagnostics.Add(new StateDiagnostic(
                    StateDiagnosticSeverity.Error,
                    "Repair analysis could not determine the underlying recovery phase."));
                return;

            default:
                diagnostics.Add(new StateDiagnostic(
                    StateDiagnosticSeverity.Error,
                    $"Run state status '{effectiveStatus}' is not supported for operator diagnostics."));
                return;
        }
    }

    private static void AnalyzeMissingRunState(
        string defaultBranch,
        List<QueueItem> inProgressItems,
        RunMetadata? metadata,
        bool runMetadataLoaded,
        GitObservation git,
        List<StateDiagnostic> diagnostics,
        StateRepairPlanBuilder repairPlanBuilder)
    {
        int experimentCount = metadata?.Experiments.Count ?? 0;
        if (experimentCount == 0 && inProgressItems.Count == 0)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Info,
                "run-state.json is not present. Hone will create it on the next loop run."));
            return;
        }

        diagnostics.Add(new StateDiagnostic(
            StateDiagnosticSeverity.Warning,
            "run-state.json is missing."));

        if (!git.HasCoreState)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                "Git state could not be read, so run-state.json cannot be reconstructed safely."));
            return;
        }

        if (git.IsWorkingTreeClean == false)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                "The working tree is dirty, so run-state.json cannot be reconstructed safely."));
            return;
        }

        string expectedStableBranch = runMetadataLoaded
            ? DetermineStableBranchFromMetadata(metadata, defaultBranch, ignoreExperiment: null)
            : defaultBranch;

        if (!string.Equals(git.CurrentBranch, expectedStableBranch, StringComparison.Ordinal))
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Warning,
                $"Current branch '{git.CurrentBranch}' does not match expected stable branch '{expectedStableBranch}'."));

            return;
        }

        foreach (QueueItem item in inProgressItems)
        {
            repairPlanBuilder.ReleaseQueueItem(
                item.Id,
                $"Release queue item '{item.Id}' back to pending.");
        }

        if (git.HeadSha is not null)
        {
            repairPlanBuilder.SaveRunState(
                CreateIdleRunStateDocument(expectedStableBranch, git.HeadSha),
                $"Create idle run-state.json for branch '{expectedStableBranch}'.");
        }
    }

    private static void AnalyzeIdleState(
        RunStateDocument runState,
        IReadOnlyList<QueueItem> inProgressItems,
        bool gitCoreAvailable,
        GitObservation git,
        List<StateDiagnostic> diagnostics,
        StateRepairPlanBuilder repairPlanBuilder)
    {
        if (runState.CurrentExperiment is not null)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Warning,
                $"run-state.json is idle but still records experiment {runState.CurrentExperiment.Number}."));
            repairPlanBuilder.SaveRunState(
                CreateIdleRunStateDocument(runState.StableBranch, runState.StableHeadSha),
                "Clear stale currentExperiment from idle run-state.json.");
        }

        foreach (QueueItem item in inProgressItems)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Warning,
                $"Optimization queue still has in-progress item '{item.Id}' while run-state.json is idle."));
            repairPlanBuilder.ReleaseQueueItem(
                item.Id,
                $"Release queue item '{item.Id}' back to pending.");
        }

        if (!gitCoreAvailable)
        {
            return;
        }

        if (git.IsWorkingTreeClean == false)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"Stable branch '{runState.StableBranch}' is dirty."));
            return;
        }

        if (!string.Equals(git.CurrentBranch, runState.StableBranch, StringComparison.Ordinal))
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Warning,
                $"Current branch '{git.CurrentBranch}' does not match stable branch '{runState.StableBranch}'."));

            if (git.StableBranchExists == true)
            {
                repairPlanBuilder.CheckoutBranch(
                    runState.StableBranch,
                    $"Check out stable branch '{runState.StableBranch}'.");
            }

            return;
        }

        if (!string.Equals(git.HeadSha, runState.StableHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"Current HEAD '{git.HeadSha}' does not match stableHeadSha '{runState.StableHeadSha}' recorded in run-state.json."));
            return;
        }

        if (runState.Status == RecoveryState.RepairRequired && !repairPlanBuilder.HasSaveRunState)
        {
            repairPlanBuilder.SaveRunState(
                CreateIdleRunStateDocument(runState.StableBranch, runState.StableHeadSha),
                "Clear repair_required and persist an idle run-state.json.");
        }

        diagnostics.Add(new StateDiagnostic(
            StateDiagnosticSeverity.Info,
            "State is consistent and idle."));
    }

    private static void AnalyzePreCandidateState(
        RunStateDocument runState,
        CurrentExperimentState currentExperiment,
        QueueItem leasedQueueItem,
        bool gitCoreAvailable,
        GitObservation git,
        List<StateDiagnostic> diagnostics,
        StateRepairPlanBuilder repairPlanBuilder)
    {
        if (!gitCoreAvailable)
        {
            return;
        }

        if (git.IsWorkingTreeClean == false)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"Cannot repair experiment {currentExperiment.Number} because the working tree is dirty."));
            return;
        }

        if (string.Equals(git.CurrentBranch, runState.StableBranch, StringComparison.Ordinal))
        {
            if (!string.Equals(git.HeadSha, runState.StableHeadSha, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new StateDiagnostic(
                    StateDiagnosticSeverity.Error,
                    $"Current HEAD '{git.HeadSha}' does not match stableHeadSha '{runState.StableHeadSha}' for pre-candidate experiment {currentExperiment.Number}."));
                return;
            }

            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Warning,
                $"Experiment {currentExperiment.Number} has no candidate commit and can be released safely."));
            repairPlanBuilder.ReleaseQueueItem(
                leasedQueueItem.Id,
                $"Release queue item '{leasedQueueItem.Id}' back to pending.");
            repairPlanBuilder.SaveRunState(
                CreateIdleRunStateDocument(runState.StableBranch, runState.StableHeadSha),
                $"Save run-state.json as idle after releasing experiment {currentExperiment.Number}.");
            return;
        }

        if (string.Equals(git.CurrentBranch, currentExperiment.BranchName, StringComparison.Ordinal))
        {
            if (string.Equals(git.HeadSha, runState.StableHeadSha, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new StateDiagnostic(
                    StateDiagnosticSeverity.Warning,
                    $"Experiment {currentExperiment.Number} is still checked out on '{currentExperiment.BranchName}' before a candidate commit was created."));
                repairPlanBuilder.CheckoutBranch(
                    runState.StableBranch,
                    $"Check out stable branch '{runState.StableBranch}' before releasing experiment {currentExperiment.Number}.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(currentExperiment.CandidateHeadSha) &&
                !string.Equals(git.HeadSha, currentExperiment.CandidateHeadSha, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new StateDiagnostic(
                    StateDiagnosticSeverity.Error,
                    $"Experiment branch '{currentExperiment.BranchName}' is at '{git.HeadSha}', not recorded candidateHeadSha '{currentExperiment.CandidateHeadSha}'."));
                return;
            }

            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Warning,
                $"Experiment {currentExperiment.Number} has an observed candidate commit on '{currentExperiment.BranchName}'."));
            CurrentExperimentState updatedExperiment = currentExperiment with
            {
                CandidateHeadSha = git.HeadSha,
                PendingOutcome = null,
                Phase = RecoveryState.CandidateCommitted,
            };
            repairPlanBuilder.MarkQueueItemInProgress(
                leasedQueueItem.Id,
                currentExperiment.Number,
                $"Mark queue item '{leasedQueueItem.Id}' as in progress.");
            repairPlanBuilder.SaveRunState(
                runState with
                {
                    Status = RecoveryState.CandidateCommitted,
                    CurrentExperiment = updatedExperiment,
                },
                $"Promote experiment {currentExperiment.Number} to candidate_committed.");
            return;
        }

        diagnostics.Add(new StateDiagnostic(
            StateDiagnosticSeverity.Error,
            $"Current branch '{git.CurrentBranch}' is neither stable branch '{runState.StableBranch}' nor experiment branch '{currentExperiment.BranchName}'."));
    }

    private static void AnalyzeCandidateCommittedState(
        RunStateDocument runState,
        CurrentExperimentState currentExperiment,
        bool gitCoreAvailable,
        GitObservation git,
        List<StateDiagnostic> diagnostics,
        StateRepairPlanBuilder repairPlanBuilder)
    {
        if (!gitCoreAvailable)
        {
            return;
        }

        if (git.IsWorkingTreeClean == false)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"Cannot resume candidate_committed experiment {currentExperiment.Number} because the working tree is dirty."));
            return;
        }

        if (string.Equals(git.CurrentBranch, currentExperiment.BranchName, StringComparison.Ordinal))
        {
            if (string.Equals(git.HeadSha, runState.StableHeadSha, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new StateDiagnostic(
                    StateDiagnosticSeverity.Error,
                    $"Experiment branch '{currentExperiment.BranchName}' is still at stableHeadSha '{runState.StableHeadSha}'."));
                return;
            }

            if (!string.IsNullOrWhiteSpace(currentExperiment.CandidateHeadSha) &&
                !string.Equals(git.HeadSha, currentExperiment.CandidateHeadSha, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new StateDiagnostic(
                    StateDiagnosticSeverity.Error,
                    $"Experiment branch '{currentExperiment.BranchName}' is at '{git.HeadSha}', not recorded candidateHeadSha '{currentExperiment.CandidateHeadSha}'."));
                return;
            }

            if (runState.Status == RecoveryState.RepairRequired)
            {
                CurrentExperimentState updatedExperiment = currentExperiment with
                {
                    CandidateHeadSha = git.HeadSha,
                    PendingOutcome = null,
                    Phase = RecoveryState.CandidateCommitted,
                };
                repairPlanBuilder.SaveRunState(
                    runState with
                    {
                        Status = RecoveryState.CandidateCommitted,
                        CurrentExperiment = updatedExperiment,
                    },
                    $"Clear repair_required for candidate_committed experiment {currentExperiment.Number}.");
            }

            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Info,
                $"Experiment {currentExperiment.Number} is recoverable on '{currentExperiment.BranchName}'. The next hone run can resume verification."));
            return;
        }

        if (string.Equals(git.CurrentBranch, runState.StableBranch, StringComparison.Ordinal))
        {
            if (git.ExperimentBranchExists == false)
            {
                diagnostics.Add(new StateDiagnostic(
                    StateDiagnosticSeverity.Error,
                    $"Experiment branch '{currentExperiment.BranchName}' does not exist locally."));
                return;
            }

            if (runState.Status == RecoveryState.RepairRequired)
            {
                repairPlanBuilder.SaveRunState(
                    runState with
                    {
                        Status = RecoveryState.CandidateCommitted,
                        CurrentExperiment = currentExperiment with
                        {
                            Phase = RecoveryState.CandidateCommitted,
                            PendingOutcome = null,
                        },
                    },
                    $"Clear repair_required for candidate_committed experiment {currentExperiment.Number}.");
            }

            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Info,
                $"Experiment {currentExperiment.Number} is recoverable from stable branch '{runState.StableBranch}'. The next hone run can check out '{currentExperiment.BranchName}' and resume verification."));
            return;
        }

        diagnostics.Add(new StateDiagnostic(
            StateDiagnosticSeverity.Error,
            $"Current branch '{git.CurrentBranch}' is neither stable branch '{runState.StableBranch}' nor experiment branch '{currentExperiment.BranchName}'."));
    }

    private static void AnalyzeFinalizingState(
        RunStateDocument runState,
        CurrentExperimentState currentExperiment,
        QueueItem finalizingQueueItem,
        RunMetadata? metadata,
        bool runMetadataLoaded,
        bool gitCoreAvailable,
        GitObservation git,
        List<StateDiagnostic> diagnostics,
        StateRepairPlanBuilder repairPlanBuilder)
    {
        if (!runMetadataLoaded)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"run-metadata.json must be readable before finalizing experiment {currentExperiment.Number} can be repaired."));
            return;
        }

        ExperimentMetadata[] matches =
        [
            .. (metadata?.Experiments ?? [])
                .Where(experiment => experiment.Experiment == currentExperiment.Number),
        ];

        if (matches.Length == 0)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"run-metadata.json does not contain experiment {currentExperiment.Number}."));
            return;
        }

        if (matches.Length > 1)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"run-metadata.json contains duplicate entries for experiment {currentExperiment.Number}."));
            return;
        }

        ExperimentMetadata finalizedExperiment = matches[0];
        if (!finalizedExperiment.Outcome.HasValue)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"run-metadata.json does not record an outcome for experiment {currentExperiment.Number}."));
            return;
        }

        if (currentExperiment.PendingOutcome.HasValue &&
            currentExperiment.PendingOutcome.Value != finalizedExperiment.Outcome.Value)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"run-state.json records pending outcome '{currentExperiment.PendingOutcome.Value}', but run-metadata.json records '{finalizedExperiment.Outcome.Value}'."));
            return;
        }

        if (finalizingQueueItem.Status != QueueItemStatus.Done)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"Queue item '{finalizingQueueItem.Id}' is {finalizingQueueItem.Status} instead of done."));
            return;
        }

        if (!gitCoreAvailable)
        {
            return;
        }

        if (git.IsWorkingTreeClean == false)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"Cannot complete finalizing experiment {currentExperiment.Number} because the working tree is dirty."));
            return;
        }

        if (IsAcceptedOutcome(finalizedExperiment.Outcome.Value))
        {
            if (!string.Equals(git.CurrentBranch, currentExperiment.BranchName, StringComparison.Ordinal))
            {
                diagnostics.Add(new StateDiagnostic(
                    StateDiagnosticSeverity.Error,
                    $"Accepted finalizing experiment {currentExperiment.Number} must be on branch '{currentExperiment.BranchName}', but current branch is '{git.CurrentBranch}'."));
                return;
            }

            if (git.HeadSha is null)
            {
                return;
            }

            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Warning,
                $"Accepted finalizing experiment {currentExperiment.Number} can be completed safely."));
            repairPlanBuilder.SaveRunState(
                CreateIdleRunStateDocument(currentExperiment.BranchName, git.HeadSha),
                $"Finalize experiment {currentExperiment.Number} and mark '{currentExperiment.BranchName}' as the new stable branch.");
            return;
        }

        if (!string.Equals(git.CurrentBranch, runState.StableBranch, StringComparison.Ordinal))
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Warning,
                $"Rejected finalizing experiment {currentExperiment.Number} should be on stable branch '{runState.StableBranch}', but current branch is '{git.CurrentBranch}'."));

            if (git.StableBranchExists == true)
            {
                repairPlanBuilder.CheckoutBranch(
                    runState.StableBranch,
                    $"Check out stable branch '{runState.StableBranch}' before clearing finalizing state.");
            }

            return;
        }

        if (!string.Equals(git.HeadSha, runState.StableHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"Rejected finalizing experiment {currentExperiment.Number} expects stableHeadSha '{runState.StableHeadSha}', but current HEAD is '{git.HeadSha}'."));
            return;
        }

        diagnostics.Add(new StateDiagnostic(
            StateDiagnosticSeverity.Warning,
            $"Rejected finalizing experiment {currentExperiment.Number} can be completed safely."));
        repairPlanBuilder.SaveRunState(
            CreateIdleRunStateDocument(runState.StableBranch, runState.StableHeadSha),
            $"Clear finalizing state for rejected experiment {currentExperiment.Number}.");
    }

    private static bool TryValidateActiveState(
        RunStateDocument runState,
        CurrentExperimentState? currentExperiment,
        RecoveryState effectiveStatus,
        OptimizationQueue queue,
        bool queueLoaded,
        IReadOnlyList<QueueItem> inProgressItems,
        RunMetadata? metadata,
        bool runMetadataLoaded,
        List<StateDiagnostic> diagnostics,
        StateRepairPlanBuilder repairPlanBuilder,
        out QueueItem? queueItem)
    {
        queueItem = null;

        if (currentExperiment is null)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"Run state status '{effectiveStatus}' is missing currentExperiment."));
            return false;
        }

        if (currentExperiment.Number <= 0)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                "run-state.json currentExperiment.number must be greater than zero."));
            return false;
        }

        if (string.IsNullOrWhiteSpace(currentExperiment.BranchName))
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"Current experiment {currentExperiment.Number} is missing an experiment branch."));
            return false;
        }

        if (string.IsNullOrWhiteSpace(currentExperiment.BaseBranch))
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"Current experiment {currentExperiment.Number} is missing a base branch."));
            return false;
        }

        if (!string.Equals(currentExperiment.BaseBranch, runState.StableBranch, StringComparison.Ordinal))
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"Current experiment {currentExperiment.Number} expects base branch '{currentExperiment.BaseBranch}', but run-state.json records stable branch '{runState.StableBranch}'."));
            return false;
        }

        if (string.IsNullOrWhiteSpace(currentExperiment.QueueItemId))
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"Current experiment {currentExperiment.Number} is missing a queue item lease."));
            return false;
        }

        if (runMetadataLoaded)
        {
            int lastFinalizedExperiment = metadata?.Experiments.Count > 0
                ? metadata.Experiments.Max(experiment => experiment.Experiment)
                : 0;
            int currentExperimentMatches = CountMatchingExperiments(metadata, currentExperiment.Number);
            int expectedExperimentNumber = effectiveStatus == RecoveryState.Finalizing && currentExperimentMatches == 1
                ? currentExperiment.Number
                : lastFinalizedExperiment + 1;

            if (currentExperiment.Number != expectedExperimentNumber)
            {
                diagnostics.Add(new StateDiagnostic(
                    StateDiagnosticSeverity.Error,
                    $"Current experiment {currentExperiment.Number} does not follow the last finalized experiment {expectedExperimentNumber - 1} from run-metadata.json."));
                return false;
            }

            if (effectiveStatus != RecoveryState.Finalizing && currentExperimentMatches > 0)
            {
                diagnostics.Add(new StateDiagnostic(
                    StateDiagnosticSeverity.Error,
                    $"run-metadata.json already contains experiment {currentExperiment.Number}, but run-state.json still owns it."));
                return false;
            }
        }

        if (!queueLoaded)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                "The optimization queue could not be read, so active lease validation is incomplete."));
            return false;
        }

        queueItem = FindQueueItem(queue, currentExperiment.QueueItemId);
        if (queueItem is null)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"Optimization queue is missing leased item '{currentExperiment.QueueItemId}' for experiment {currentExperiment.Number}."));
            return false;
        }

        foreach (QueueItem inProgressItem in inProgressItems)
        {
            if (!string.Equals(inProgressItem.Id, currentExperiment.QueueItemId, StringComparison.Ordinal))
            {
                diagnostics.Add(new StateDiagnostic(
                    StateDiagnosticSeverity.Warning,
                    $"Optimization queue has unrelated in-progress item '{inProgressItem.Id}'."));
                repairPlanBuilder.ReleaseQueueItem(
                    inProgressItem.Id,
                    $"Release unrelated queue item '{inProgressItem.Id}' back to pending.");
            }
        }

        if (effectiveStatus == RecoveryState.Finalizing)
        {
            if (queueItem.Status != QueueItemStatus.Done)
            {
                diagnostics.Add(new StateDiagnostic(
                    StateDiagnosticSeverity.Error,
                    $"Queue item '{queueItem.Id}' is {queueItem.Status} instead of done."));
                return false;
            }

            return true;
        }

        if (queueItem.Status is QueueItemStatus.Done or QueueItemStatus.Skipped)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Error,
                $"Optimization queue item '{queueItem.Id}' is already marked {queueItem.Status}, but run-state.json still owns it."));
            return false;
        }

        if (queueItem.Status != QueueItemStatus.InProgress)
        {
            diagnostics.Add(new StateDiagnostic(
                StateDiagnosticSeverity.Warning,
                $"Queue item '{queueItem.Id}' is {queueItem.Status} instead of in_progress."));
            repairPlanBuilder.MarkQueueItemInProgress(
                queueItem.Id,
                currentExperiment.Number,
                $"Mark queue item '{queueItem.Id}' as in progress.");
        }

        return true;
    }

    private static int CountMatchingExperiments(RunMetadata? metadata, int experimentNumber) =>
        metadata?.Experiments.Count(experiment => experiment.Experiment == experimentNumber) ?? 0;

    private static QueueItem? FindQueueItem(OptimizationQueue queue, string queueItemId) =>
        queue.Items.FirstOrDefault(item => string.Equals(item.Id, queueItemId, StringComparison.Ordinal));

    private static List<QueueItem> CollectInProgressItems(OptimizationQueue queue) =>
    [
        .. queue.Items.Where(item => item.Status == QueueItemStatus.InProgress),
    ];

    private static string DetermineStableBranchFromMetadata(
        RunMetadata? metadata,
        string defaultBranch,
        int? ignoreExperiment)
    {
        ArgumentException.ThrowIfNullOrEmpty(defaultBranch);

        string stableBranch = defaultBranch;
        if (metadata is null)
        {
            return stableBranch;
        }

        foreach (ExperimentMetadata experiment in metadata.Experiments
            .Where(experiment => ignoreExperiment is null || experiment.Experiment != ignoreExperiment.Value)
            .OrderBy(experiment => experiment.Experiment))
        {
            if (experiment.Outcome is ExperimentOutcome.Improved or ExperimentOutcome.EfficiencyWin &&
                !string.IsNullOrWhiteSpace(experiment.BranchName))
            {
                stableBranch = experiment.BranchName;
            }
        }

        return stableBranch;
    }

    private static RecoveryState DetermineEffectiveStatus(RunStateDocument runState) =>
        runState.Status == RecoveryState.RepairRequired
            ? runState.CurrentExperiment?.Phase ?? RecoveryState.Idle
            : runState.Status;

    private static RunStateDocument CreateIdleRunStateDocument(string stableBranch, string stableHeadSha) =>
        new()
        {
            StableBranch = stableBranch,
            StableHeadSha = stableHeadSha,
            Status = RecoveryState.Idle,
        };

    private static bool IsAcceptedOutcome(ExperimentOutcome outcome) =>
        outcome is ExperimentOutcome.Improved or ExperimentOutcome.EfficiencyWin;

    private static bool HasSameRepairSteps(StateRepairPlan left, StateRepairPlan right)
    {
        if (left.Steps.Count != right.Steps.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Steps.Count; i++)
        {
            if (!string.Equals(left.Steps[i].Description, right.Steps[i].Description, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddGitDiagnostics(GitObservation git, List<StateDiagnostic> diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(git.CurrentBranchError))
        {
            diagnostics.Add(new StateDiagnostic(StateDiagnosticSeverity.Error, git.CurrentBranchError));
        }

        if (!string.IsNullOrWhiteSpace(git.HeadShaError))
        {
            diagnostics.Add(new StateDiagnostic(StateDiagnosticSeverity.Error, git.HeadShaError));
        }

        if (!string.IsNullOrWhiteSpace(git.WorkingTreeError))
        {
            diagnostics.Add(new StateDiagnostic(StateDiagnosticSeverity.Error, git.WorkingTreeError));
        }

        if (!string.IsNullOrWhiteSpace(git.StableBranchError))
        {
            diagnostics.Add(new StateDiagnostic(StateDiagnosticSeverity.Error, git.StableBranchError));
        }

        if (!string.IsNullOrWhiteSpace(git.ExperimentBranchError))
        {
            diagnostics.Add(new StateDiagnostic(StateDiagnosticSeverity.Error, git.ExperimentBranchError));
        }
    }

    private async Task<RunMetadata?> LoadRunMetadataAsync(CancellationToken ct)
    {
        if (!File.Exists(_runMetadataPath))
        {
            return null;
        }

        try
        {
            string json = await File.ReadAllTextAsync(_runMetadataPath, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<RunMetadata>(json, RunMetadataJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse run metadata document at '{_runMetadataPath}': {ex.Message}",
                ex);
        }
    }

    private async Task<bool> IsManagedWorkingTreeCleanAsync(CancellationToken ct)
    {
        if (_versionControl is IPathFilteringVersionControl filteringVersionControl)
        {
            return await filteringVersionControl.IsWorkingTreeCleanAsync(
                _targetDir,
                [_managedResultsPath],
                ct).ConfigureAwait(false);
        }

        return await _versionControl.IsWorkingTreeCleanAsync(_targetDir, ct).ConfigureAwait(false);
    }

    private async Task<GitObservation> ObserveGitAsync(
        string? stableBranch,
        string? experimentBranch,
        CancellationToken ct)
    {
        string? currentBranch = null;
        string? headSha = null;
        bool? isWorkingTreeClean = null;
        bool? stableBranchExists = null;
        bool? experimentBranchExists = null;
        string? currentBranchError = null;
        string? headShaError = null;
        string? workingTreeError = null;
        string? stableBranchError = null;
        string? experimentBranchError = null;

        try
        {
            currentBranch = await _versionControl.GetCurrentBranchAsync(_targetDir, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            currentBranchError = ex.Message;
        }

        try
        {
            headSha = await _versionControl.GetHeadShaAsync(_targetDir, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            headShaError = ex.Message;
        }

        try
        {
            isWorkingTreeClean = await IsManagedWorkingTreeCleanAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            workingTreeError = ex.Message;
        }

        if (!string.IsNullOrWhiteSpace(stableBranch))
        {
            try
            {
                stableBranchExists = await _versionControl.LocalBranchExistsAsync(_targetDir, stableBranch, ct)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                stableBranchError = ex.Message;
            }
        }

        if (!string.IsNullOrWhiteSpace(experimentBranch))
        {
            try
            {
                experimentBranchExists = await _versionControl.LocalBranchExistsAsync(_targetDir, experimentBranch, ct)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                experimentBranchError = ex.Message;
            }
        }

        return new GitObservation(
            currentBranch,
            headSha,
            isWorkingTreeClean,
            stableBranchExists,
            experimentBranchExists,
            currentBranchError,
            headShaError,
            workingTreeError,
            stableBranchError,
            experimentBranchError);
    }

    private async Task ApplyRepairStepAsync(StateRepairStep step, CancellationToken ct)
    {
        switch (step)
        {
            case CheckoutBranchRepairStep checkoutBranch:
                await _versionControl.CheckoutAsync(_targetDir, checkoutBranch.Branch, create: false, ct)
                    .ConfigureAwait(false);
                return;

            case ReleaseQueueItemRepairStep releaseQueueItem:
                _queueManager.ReleaseLease(releaseQueueItem.ItemId);
                return;

            case MarkQueueItemInProgressRepairStep markInProgress:
                _queueManager.MarkInProgress(markInProgress.ItemId, markInProgress.Experiment);
                return;

            case SaveRunStateRepairStep saveRunState:
                await _runStateStore.SaveAsync(saveRunState.Document, ct).ConfigureAwait(false);
                return;

            default:
                throw new InvalidOperationException($"Unsupported repair step '{step.GetType().Name}'.");
        }
    }

    private sealed class StateRepairPlanBuilder
    {
        private readonly Dictionary<string, ReleaseQueueItemRepairStep> _releaseSteps = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MarkQueueItemInProgressRepairStep> _markInProgressSteps = new(StringComparer.Ordinal);
        private CheckoutBranchRepairStep? _checkoutBranch;
        private SaveRunStateRepairStep? _saveRunState;

        public bool HasSaveRunState => _saveRunState is not null;

        public void CheckoutBranch(string branch, string description)
        {
            ArgumentException.ThrowIfNullOrEmpty(branch);
            ArgumentException.ThrowIfNullOrEmpty(description);

            _checkoutBranch ??= new CheckoutBranchRepairStep(branch, description);
        }

        public void ReleaseQueueItem(string itemId, string description)
        {
            ArgumentException.ThrowIfNullOrEmpty(itemId);
            ArgumentException.ThrowIfNullOrEmpty(description);

            _ = _markInProgressSteps.Remove(itemId);
            _releaseSteps[itemId] = new ReleaseQueueItemRepairStep(itemId, description);
        }

        public void MarkQueueItemInProgress(string itemId, int? experiment, string description)
        {
            ArgumentException.ThrowIfNullOrEmpty(itemId);
            ArgumentException.ThrowIfNullOrEmpty(description);

            if (_releaseSteps.ContainsKey(itemId))
            {
                return;
            }

            _markInProgressSteps[itemId] = new MarkQueueItemInProgressRepairStep(itemId, experiment, description);
        }

        public void SaveRunState(RunStateDocument document, string description)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentException.ThrowIfNullOrEmpty(description);

            _saveRunState = new SaveRunStateRepairStep(document, description);
        }

        public StateRepairPlan Build()
        {
            var steps = new List<StateRepairStep>();
            if (_checkoutBranch is not null)
            {
                steps.Add(_checkoutBranch);
            }

            steps.AddRange(_releaseSteps.Values.OrderBy(step => step.ItemId, StringComparer.Ordinal));
            steps.AddRange(_markInProgressSteps.Values.OrderBy(step => step.ItemId, StringComparer.Ordinal));

            if (_saveRunState is not null)
            {
                steps.Add(_saveRunState);
            }

            return steps.Count == 0 ? StateRepairPlan.None : new StateRepairPlan(steps);
        }
    }
}

internal enum StateDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

internal sealed record StateDiagnostic(StateDiagnosticSeverity Severity, string Message);

internal sealed record StatePaths(string RunStatePath, string QueuePath, string RunMetadataPath);

internal sealed record StateInspectionResult(
    string TargetDir,
    StatePaths Paths,
    bool RunStateExists,
    bool QueueExists,
    bool RunMetadataExists,
    RunStateDocument? RunState,
    OptimizationQueue Queue,
    RunMetadata? Metadata,
    GitObservation Git,
    IReadOnlyList<StateDiagnostic> Diagnostics,
    StateRepairPlan RepairPlan)
{
    public int InProgressItemCount => Queue.Items.Count(item => item.Status == QueueItemStatus.InProgress);

    public int MetadataExperimentCount => Metadata?.Experiments.Count ?? 0;

    public bool RequiresAttention =>
        Diagnostics.Any(diagnostic => diagnostic.Severity is StateDiagnosticSeverity.Warning or StateDiagnosticSeverity.Error);
}

internal sealed record StateRepairResult(
    StateInspectionResult Before,
    IReadOnlyList<string> AppliedSteps,
    StateInspectionResult After,
    IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0 && !After.RequiresAttention;
}

internal sealed record GitObservation(
    string? CurrentBranch,
    string? HeadSha,
    bool? IsWorkingTreeClean,
    bool? StableBranchExists,
    bool? ExperimentBranchExists,
    string? CurrentBranchError,
    string? HeadShaError,
    string? WorkingTreeError,
    string? StableBranchError,
    string? ExperimentBranchError)
{
    public bool HasCoreState =>
        !string.IsNullOrWhiteSpace(CurrentBranch) &&
        !string.IsNullOrWhiteSpace(HeadSha) &&
        IsWorkingTreeClean.HasValue;

    public string Summary
    {
        get
        {
            if (!HasCoreState)
            {
                return "unavailable";
            }

            string shortSha = HeadSha!.Length <= 12 ? HeadSha : HeadSha[..12];
            string cleanliness = IsWorkingTreeClean == true ? "clean" : "dirty";
            return $"branch '{CurrentBranch}', head '{shortSha}', {cleanliness}";
        }
    }
}

internal sealed record StateRepairPlan(IReadOnlyList<StateRepairStep> Steps)
{
    public static StateRepairPlan None { get; } = new([]);

    public bool HasSteps => Steps.Count > 0;
}

internal abstract record StateRepairStep(string Description);

internal sealed record CheckoutBranchRepairStep(string Branch, string Description)
    : StateRepairStep(Description);

internal sealed record ReleaseQueueItemRepairStep(string ItemId, string Description)
    : StateRepairStep(Description);

internal sealed record MarkQueueItemInProgressRepairStep(string ItemId, int? Experiment, string Description)
    : StateRepairStep(Description);

internal sealed record SaveRunStateRepairStep(RunStateDocument Document, string Description)
    : StateRepairStep(Description);
