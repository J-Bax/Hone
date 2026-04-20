using System.Diagnostics;
using System.Globalization;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Orchestration.Artifacts;
using Hone.Orchestration.Failure;
using Hone.Orchestration.Implementer;
using Hone.Orchestration.Queue;
using Hone.Orchestration.State;

namespace Hone.Orchestration.Loop;

/// <summary>
/// Main entry point for the Hone optimization loop.
/// Orchestrates analysis → queue → implement → verify → accept/reject for each experiment.
/// </summary>
internal sealed class HoneLoopRunner
{
    private readonly ILoopPipeline _pipeline;
    private readonly OptimizationQueueManager _queueManager;
    private readonly IterativeImplementerRunner _implementer;
    private readonly ExperimentFailureHandler _failureHandler;
    private readonly IVersionControl _versionControl;
    private readonly IRunStateStore _runStateStore;
    private readonly IHoneEventSink _eventSink;

    internal HoneLoopRunner(
        ILoopPipeline pipeline,
        OptimizationQueueManager queueManager,
        IterativeImplementerRunner implementer,
        ExperimentFailureHandler failureHandler,
        IVersionControl versionControl,
        IRunStateStore runStateStore,
        IHoneEventSink eventSink)
    {
        _pipeline = pipeline;
        _queueManager = queueManager;
        _implementer = implementer;
        _failureHandler = failureHandler;
        _versionControl = versionControl;
        _runStateStore = runStateStore;
        _eventSink = eventSink;
    }

    /// <summary>
    /// Runs the optimization loop end-to-end.
    /// </summary>
    internal async Task<LoopResult> RunAsync(LoopOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        HoneConfig config = options.Config;
        string targetDir = options.TargetDir;
        string resultsPath = options.ResultsPath;
        string metadataPath = Path.Combine(targetDir, resultsPath, "run-metadata.json");

        _eventSink.Emit(new PhaseStarted("loop", DateTimeOffset.UtcNow, Experiment: null));
        var sw = Stopwatch.StartNew();

        // ── Load durable state before any new work is leased ─────────────────
        RunMetadata? existingMetadata = await _pipeline.LoadRunMetadataAsync(metadataPath, ct)
            .ConfigureAwait(false);
        string targetName = options.TargetName
            ?? existingMetadata?.TargetName
            ?? "unknown";
        var experiments = new List<ExperimentMetadata>(existingMetadata?.Experiments ?? []);
        int priorCount = experiments.Count;

        LoopState startupState = CreateResumedState(
            options.DefaultBranch,
            experiments,
            initialBestP95: double.PositiveInfinity);
        RunStateDocument? runState = await _runStateStore.LoadAsync(ct).ConfigureAwait(false);
        if (runState is null)
        {
            RepositoryPreflightResult preflight = await PreflightRepositoryAccessAsync(
                targetDir,
                resultsPath,
                ct).ConfigureAwait(false);
            if (!preflight.Success)
            {
                return StopForPreflightFailure(preflight.Message, startupState, sw);
            }
        }

        StartupGateResult startupGate = await ValidateStartupAsync(
            targetDir,
            resultsPath,
            experiments,
            startupState,
            runState,
            ct).ConfigureAwait(false);
        if (!startupGate.CanContinue)
        {
            return await StopForRepairRequiredAsync(startupGate, startupState, sw, ct)
                .ConfigureAwait(false);
        }

        if (startupGate.UpdatedRunState is not null)
        {
            await _runStateStore.SaveAsync(startupGate.UpdatedRunState, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(startupGate.Message))
        {
            _eventSink.Emit(new StatusMessage(
                startupGate.Message,
                LogLevel.Warning,
                DateTimeOffset.UtcNow,
                startupGate.Experiment));
        }

        // ── Prepare (once per run, before any experiments) ──────────────────
        HookResult prepareResult = await _pipeline.PrepareAsync(targetDir, config, ct)
            .ConfigureAwait(false);
        if (!prepareResult.Success)
        {
            _eventSink.Emit(new StatusMessage(
                $"Prepare hook failed: {prepareResult.Message}",
                LogLevel.Warning, DateTimeOffset.UtcNow, Experiment: null));
        }

        // ── Baseline ────────────────────────────────────────────────────────
        Uri? baselineBaseUrl = null;
        bool baselineStartedTarget = false;
        if (!HasPersistedBaseline(targetDir, config))
        {
            HookResult baselineStartResult = await _pipeline.StartTargetAsync(targetDir, config, experiment: 0, ct)
                .ConfigureAwait(false);
            if (!baselineStartResult.Success)
            {
                _eventSink.Emit(new StatusMessage(
                    $"Start hook failed before baseline: {baselineStartResult.Message}",
                    LogLevel.Error, DateTimeOffset.UtcNow, Experiment: null));

                throw new InvalidOperationException(
                    $"Unable to start the target for baseline creation: {baselineStartResult.Message}");
            }

            baselineBaseUrl = baselineStartResult.BaseUrl;
            baselineStartedTarget = true;
        }

        MetricSet baseline = await _pipeline.LoadOrCreateBaselineAsync(targetDir, config, baselineBaseUrl, ct)
            .ConfigureAwait(false);
        double baselineP95 = baseline.HttpReqDuration.P95;

        // ── Machine info ────────────────────────────────────────────────────
        MachineInfo machineInfo = await _pipeline.GetMachineInfoAsync(ct)
            .ConfigureAwait(false);

        // ── Resume state from prior experiments ─────────────────────────────
        LoopState state = CreateResumedState(options.DefaultBranch, experiments, baselineP95);

        // ── Experiment loop ─────────────────────────────────────────────────
        int maxExp = options.MaxExperimentsOverride ?? config.Loop.MaxExperiments;
        bool skipNewExperiments = false;
        bool resumedExperimentRan = false;

        if (startupGate.ResumedExperiment is not null)
        {
            ExperimentContext recoveredContext = await RunRecoveredExperimentAsync(
                startupGate.ResumedExperiment,
                state,
                baseline,
                options,
                config,
                targetDir,
                resultsPath,
                targetName,
                machineInfo,
                experiments,
                metadataPath,
                ct).ConfigureAwait(false);

            resumedExperimentRan = true;
            if (recoveredContext.TargetLifecycleTakenOver)
            {
                baselineStartedTarget = false;
            }

            if (recoveredContext.ShouldBreak)
            {
                skipNewExperiments = true;
            }
        }

        int remainingExperiments = Math.Max(maxExp - (resumedExperimentRan ? 1 : 0), 0);
        int startExperiment = experiments.Count + 1;

        for (int offset = 0; !skipNewExperiments && offset < remainingExperiments; offset++)
        {
            int exp = startExperiment + offset;
            ct.ThrowIfCancellationRequested();

            // Cooldown between experiments to allow TCP TIME_WAIT sockets to clear
            // and reduce environmental degradation across consecutive load tests.
            if (exp > startExperiment && config.Loop.ExperimentCooldownSeconds > 0)
            {
                int cooldown = config.Loop.ExperimentCooldownSeconds;
                _eventSink.Emit(new StatusMessage(
                    $"Cooling down {cooldown}s between experiments",
                    LogLevel.Info, DateTimeOffset.UtcNow, exp));
                await Task.Delay(TimeSpan.FromSeconds(cooldown), ct).ConfigureAwait(false);
            }

            ExperimentContext expCtx = await RunSingleExperimentAsync(
                exp, state, baseline, options, config, targetDir, resultsPath,
                targetName, machineInfo, experiments, metadataPath, ct)
                .ConfigureAwait(false);

            if (expCtx.TargetLifecycleTakenOver)
            {
                baselineStartedTarget = false;
            }

            if (expCtx.ShouldBreak)
            {
                break;
            }
        }

        if (baselineStartedTarget)
        {
            HookResult stopResult = await _pipeline.StopTargetAsync(targetDir, config, experiment: 0, ct)
                .ConfigureAwait(false);
            if (!stopResult.Success)
            {
                _eventSink.Emit(new StatusMessage(
                    $"Stop hook failed: {stopResult.Message}",
                    LogLevel.Warning, DateTimeOffset.UtcNow, Experiment: null));
            }
        }

        // ── Cleanup (once per run, after all experiments) ───────────────────
        HookResult cleanupResult = await _pipeline.CleanupAsync(targetDir, config, ct)
            .ConfigureAwait(false);
        if (!cleanupResult.Success)
        {
            _eventSink.Emit(new StatusMessage(
                $"Cleanup hook failed: {cleanupResult.Message}",
                LogLevel.Warning, DateTimeOffset.UtcNow, Experiment: null));
        }

        // ── Finalise ────────────────────────────────────────────────────────
        sw.Stop();
        _eventSink.Emit(new PhaseCompleted(
            "loop", sw.Elapsed, Success: true,
            DateTimeOffset.UtcNow, Experiment: null));

        return new LoopResult(
            ExitReason: state.ExitReason,
            ExperimentsRun: experiments.Count - priorCount,
            SuccessCount: state.SuccessCount,
            BestP95: state.BestP95,
            BestExperiment: state.BestExperiment,
            BaselineP95: baselineP95,
            PrChain: [.. state.PrChain],
            BranchChain: [.. state.BranchChain],
            FailedExperiments: [.. state.FailedExperiments]);
    }

    // ── Single experiment orchestration ─────────────────────────────────────

    private async Task<ExperimentContext> RunSingleExperimentAsync(
        int exp,
        LoopState state,
        MetricSet baseline,
        LoopOptions options,
        HoneConfig config,
        string targetDir,
        string resultsPath,
        string targetName,
        MachineInfo machineInfo,
        List<ExperimentMetadata> experiments,
        string metadataPath,
        CancellationToken ct)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        _eventSink.Emit(new PhaseStarted("experiment", DateTimeOffset.UtcNow, exp));

        MetricSet reference = state.PreviousMetrics ?? baseline;
        string branchName = $"{config.Loop.BranchPrefix}-{exp}";
        string baseBranch = state.CurrentBranch;
        string expectedStableHeadSha = await _versionControl.GetHeadShaAsync(targetDir, ct)
            .ConfigureAwait(false);
        string cleanupManifestPath = _runStateStore.GetCleanupManifestPath(exp);

        var ctx = new ExperimentRunContext(
            exp, startedAt, branchName, baseBranch, state, config, options,
            targetDir, resultsPath, targetName, machineInfo, experiments, metadataPath,
            expectedStableHeadSha, cleanupManifestPath);
        bool targetLifecycleTakenOver = false;
        CurrentExperimentState currentExperiment;

        // ── Analyse (if queue empty) ────────────────────────────────────────
        if (!_queueManager.HasActionable())
        {
            bool analysisOk = await TryAnalyseAsync(exp, baseline, state, targetDir, ct)
                .ConfigureAwait(false);
            if (!analysisOk)
            {
                state.ExitReason = "no_opportunities";
                return new ExperimentContext(ShouldBreak: true, TargetLifecycleTakenOver: targetLifecycleTakenOver);
            }
        }

        // ── Pick queue item & classify ──────────────────────────────────────
        // Inner loop: skip architecture items without consuming the experiment counter.
        QueueItem? item;
        while (true)
        {
            item = _queueManager.GetNext(exp);
            if (item is null)
            {
                state.ExitReason = "queue_exhausted";
                return new ExperimentContext(ShouldBreak: true, TargetLifecycleTakenOver: targetLifecycleTakenOver);
            }

            currentExperiment = CreateLeasedExperimentState(ctx, item);
            await SaveRunStateAsync(
                ctx.BaseBranch,
                ctx.ExpectedStableHeadSha,
                RecoveryState.ExperimentLeased,
                currentExperiment,
                ct).ConfigureAwait(false);

            if (config.Loop.SkipClassification)
            {
                break;
            }

            ClassificationResult classResult = await _pipeline.ClassifyAsync(
                new ClassificationInput(item.FilePath, item.Explanation, exp, targetDir), ct)
                .ConfigureAwait(false);

            if (classResult.Success && classResult.Scope is OpportunityScope.Architecture)
            {
                _queueManager.MarkDone(item.Id, "skipped_architecture", exp);
                await SaveIdleRunStateAsync(ctx.BaseBranch, ctx.ExpectedStableHeadSha, ct)
                    .ConfigureAwait(false);
                continue;
            }

            break;
        }

        // ── Stop target API before build ────────────────────────────────────
        // Prevents file-lock failures on Windows when the API runs from bin/Release.
        _eventSink.Emit(new StatusMessage(
            "Stopping target API for build", LogLevel.Info, DateTimeOffset.UtcNow, exp));
        targetLifecycleTakenOver = true;
        HookResult stopResult = await _pipeline.StopTargetAsync(targetDir, config, exp, ct)
            .ConfigureAwait(false);
        if (!stopResult.Success)
        {
            _eventSink.Emit(new StatusMessage(
                $"Stop hook failed: {stopResult.Message}", LogLevel.Warning, DateTimeOffset.UtcNow, exp));
        }

        // ── Implement ───────────────────────────────────────────────────────
        string? rcaDocument = _queueManager.GetRootCauseDocument(item.Id);
        ImplementerRunResult implResult = await _implementer.RunAsync(
            new ImplementerOptions(
                FilePath: item.FilePath,
                Explanation: item.Explanation,
                RootCauseDocument: rcaDocument,
                Experiment: exp,
                BaseBranch: baseBranch,
                TargetDir: targetDir,
                TargetName: targetName,
                Config: config.Implementer,
                CriticConfig: config.Critic,
                TestProjectPaths: null,
                BranchPrefix: config.Loop.BranchPrefix,
                 ResultsPath: resultsPath,
                 ClassificationScope: item.Scope.ToString().ToUpperInvariant()), ct)
            .ConfigureAwait(false);

        currentExperiment = await PersistObservedPhaseAsync(
            ctx,
            currentExperiment,
            implResult.CommitSha,
            ct).ConfigureAwait(false);

        if (!implResult.Result.Success)
        {
            ExperimentOutcome failureOutcome = MapImplementerFailureOutcome(implResult.Result);

            // Restart API before next experiment even though this one failed
            await TryRestartTargetAsync(targetDir, config, exp, ct).ConfigureAwait(false);
            ExperimentContext failedContext = await HandleFailedExperimentAsync(
                ctx, failureOutcome,
                experimentMetrics: null, queueItemId: item.Id, currentExperiment, ct)
                .ConfigureAwait(false);
            return failedContext with { TargetLifecycleTakenOver = targetLifecycleTakenOver };
        }

        // ── Restart target API for load testing ─────────────────────────────
        _eventSink.Emit(new StatusMessage(
            "Restarting target API for verification", LogLevel.Info, DateTimeOffset.UtcNow, exp));
        HookResult startResult = await _pipeline.StartTargetAsync(targetDir, config, exp, ct)
            .ConfigureAwait(false);
        if (!startResult.Success)
        {
            _eventSink.Emit(new StatusMessage(
                $"Start hook failed: {startResult.Message}", LogLevel.Error, DateTimeOffset.UtcNow, exp));
            ExperimentContext failedContext = await HandleFailedExperimentAsync(
                ctx, ExperimentOutcome.StartFailed,
                experimentMetrics: null, queueItemId: item.Id, currentExperiment, ct)
                .ConfigureAwait(false);
            return failedContext with { TargetLifecycleTakenOver = targetLifecycleTakenOver };
        }

        // ── Verify ──────────────────────────────────────────────────────────
        MetricSet? experimentMetrics = await VerifyAsync(
            exp, reference, startResult.BaseUrl, options, targetDir, resultsPath, ct)
            .ConfigureAwait(false);

        if (experimentMetrics is null)
        {
            ExperimentContext failedContext = await HandleFailedExperimentAsync(
                ctx, ExperimentOutcome.LoadTestFailed,
                experimentMetrics: null, queueItemId: item.Id, currentExperiment, ct)
                .ConfigureAwait(false);
            return failedContext with { TargetLifecycleTakenOver = targetLifecycleTakenOver };
        }

        // ── Compare ─────────────────────────────────────────────────────────
        ComparisonResult comparison = _pipeline.CompareMetrics(
            experimentMetrics, baseline, state.PreviousMetrics, exp, config);

        _eventSink.Emit(new ExperimentOutcomeEvent(
            comparison.Outcome.ToString(), comparison, DateTimeOffset.UtcNow, exp));

        // ── Decision ────────────────────────────────────────────────────────
        return comparison.Outcome switch
        {
            ExperimentOutcome.Improved or ExperimentOutcome.EfficiencyWin =>
                (await HandleAcceptedAsync(
                    ctx, item, experimentMetrics, comparison, currentExperiment, ct).ConfigureAwait(false))
                with
                {
                    TargetLifecycleTakenOver = targetLifecycleTakenOver,
                },

            ExperimentOutcome.Regressed =>
                (await HandleFailedExperimentAsync(
                    ctx, ExperimentOutcome.Regressed, experimentMetrics, queueItemId: item.Id, currentExperiment, ct).ConfigureAwait(false))
                with
                {
                    TargetLifecycleTakenOver = targetLifecycleTakenOver,
                },

            ExperimentOutcome.Stale =>
                (await HandleFailedExperimentAsync(
                    ctx, ExperimentOutcome.Stale, experimentMetrics, queueItemId: item.Id, currentExperiment, ct).ConfigureAwait(false))
                with
                {
                    TargetLifecycleTakenOver = targetLifecycleTakenOver,
                },

            ExperimentOutcome.ImplementationFailed
                or ExperimentOutcome.BuildFailed
                or ExperimentOutcome.TestFailed
                or ExperimentOutcome.StartFailed
                or ExperimentOutcome.LoadTestFailed =>
                throw new InvalidOperationException(
                    $"Unexpected comparison outcome from metric comparison: {comparison.Outcome}"),

            ExperimentOutcome.Unknown or _ => throw new InvalidOperationException($"Unexpected experiment outcome: {comparison.Outcome}"),
        };
    }

    private async Task<ExperimentContext> RunRecoveredExperimentAsync(
        ResumedExperiment resumedExperiment,
        LoopState state,
        MetricSet baseline,
        LoopOptions options,
        HoneConfig config,
        string targetDir,
        string resultsPath,
        string targetName,
        MachineInfo machineInfo,
        List<ExperimentMetadata> experiments,
        string metadataPath,
        CancellationToken ct)
    {
        CurrentExperimentState currentExperiment = resumedExperiment.CurrentExperiment;
        int exp = currentExperiment.Number;
        DateTimeOffset startedAt = ParseStartedAt(currentExperiment.StartedAt);
        MetricSet reference = state.PreviousMetrics ?? baseline;
        string cleanupManifestPath = currentExperiment.CleanupManifestPath
            ?? _runStateStore.GetCleanupManifestPath(exp);

        var ctx = new ExperimentRunContext(
            exp,
            startedAt,
            currentExperiment.BranchName,
            currentExperiment.BaseBranch,
            state,
            config,
            options,
            targetDir,
            resultsPath,
            targetName,
            machineInfo,
            experiments,
            metadataPath,
            resumedExperiment.StableHeadSha,
            cleanupManifestPath);

        _eventSink.Emit(new StatusMessage(
            $"Resuming experiment {exp} from {currentExperiment.Phase}.",
            LogLevel.Warning,
            DateTimeOffset.UtcNow,
            exp));

        _eventSink.Emit(new StatusMessage(
            "Stopping target API before recovery verification",
            LogLevel.Info,
            DateTimeOffset.UtcNow,
            exp));
        HookResult stopResult = await _pipeline.StopTargetAsync(targetDir, config, exp, ct)
            .ConfigureAwait(false);
        if (!stopResult.Success)
        {
            _eventSink.Emit(new StatusMessage(
                $"Stop hook failed: {stopResult.Message}",
                LogLevel.Warning,
                DateTimeOffset.UtcNow,
                exp));
        }

        _eventSink.Emit(new StatusMessage(
            "Restarting target API for verification",
            LogLevel.Info,
            DateTimeOffset.UtcNow,
            exp));
        HookResult startResult = await _pipeline.StartTargetAsync(targetDir, config, exp, ct)
            .ConfigureAwait(false);
        if (!startResult.Success)
        {
            _eventSink.Emit(new StatusMessage(
                $"Start hook failed: {startResult.Message}",
                LogLevel.Error,
                DateTimeOffset.UtcNow,
                exp));
            ExperimentContext failedContext = await HandleFailedExperimentAsync(
                ctx,
                ExperimentOutcome.StartFailed,
                experimentMetrics: null,
                resumedExperiment.QueueItem.Id,
                currentExperiment,
                ct).ConfigureAwait(false);
            return failedContext with { TargetLifecycleTakenOver = true };
        }

        MetricSet? experimentMetrics = await VerifyAsync(
            exp,
            reference,
            startResult.BaseUrl,
            options,
            targetDir,
            resultsPath,
            ct).ConfigureAwait(false);
        if (experimentMetrics is null)
        {
            ExperimentContext failedContext = await HandleFailedExperimentAsync(
                ctx,
                ExperimentOutcome.LoadTestFailed,
                experimentMetrics: null,
                resumedExperiment.QueueItem.Id,
                currentExperiment,
                ct).ConfigureAwait(false);
            return failedContext with { TargetLifecycleTakenOver = true };
        }

        ComparisonResult comparison = _pipeline.CompareMetrics(
            experimentMetrics,
            baseline,
            state.PreviousMetrics,
            exp,
            config);

        _eventSink.Emit(new ExperimentOutcomeEvent(
            comparison.Outcome.ToString(),
            comparison,
            DateTimeOffset.UtcNow,
            exp));

        return comparison.Outcome switch
        {
            ExperimentOutcome.Improved or ExperimentOutcome.EfficiencyWin =>
                (await HandleAcceptedAsync(
                    ctx,
                    resumedExperiment.QueueItem,
                    experimentMetrics,
                    comparison,
                    currentExperiment,
                    ct).ConfigureAwait(false))
                with
                {
                    TargetLifecycleTakenOver = true,
                },

            ExperimentOutcome.Regressed =>
                (await HandleFailedExperimentAsync(
                    ctx,
                    ExperimentOutcome.Regressed,
                    experimentMetrics,
                    resumedExperiment.QueueItem.Id,
                    currentExperiment,
                    ct).ConfigureAwait(false))
                with
                {
                    TargetLifecycleTakenOver = true,
                },

            ExperimentOutcome.Stale =>
                (await HandleFailedExperimentAsync(
                    ctx,
                    ExperimentOutcome.Stale,
                    experimentMetrics,
                    resumedExperiment.QueueItem.Id,
                    currentExperiment,
                    ct).ConfigureAwait(false))
                with
                {
                    TargetLifecycleTakenOver = true,
                },

            ExperimentOutcome.ImplementationFailed
                or ExperimentOutcome.BuildFailed
                or ExperimentOutcome.TestFailed
                or ExperimentOutcome.StartFailed
                or ExperimentOutcome.LoadTestFailed =>
                throw new InvalidOperationException(
                    $"Unexpected comparison outcome from metric comparison: {comparison.Outcome}"),

            ExperimentOutcome.Unknown or _ => throw new InvalidOperationException(
                $"Unexpected experiment outcome: {comparison.Outcome}"),
        };
    }

    // ── Analysis ────────────────────────────────────────────────────────────

    private async Task<bool> TryAnalyseAsync(
        int experiment,
        MetricSet baseline,
        LoopState state,
        string targetDir,
        CancellationToken ct)
    {
        _eventSink.Emit(new StatusMessage(
            "Queue empty — running analysis",
            LogLevel.Info, DateTimeOffset.UtcNow, experiment));

        AnalysisResult result = await _pipeline.RunAnalysisAsync(
            new AnalysisInput(targetDir, experiment, baseline, state.PreviousMetrics), ct)
            .ConfigureAwait(false);

        if (!result.Success || result.Opportunities.Count == 0)
        {
            _eventSink.Emit(new StatusMessage(
                "Analysis returned no actionable opportunities",
                LogLevel.Warning, DateTimeOffset.UtcNow, experiment));
            return false;
        }

        _ = _queueManager.Initialize(result.Opportunities, experiment);
        return true;
    }

    // ── Verification ────────────────────────────────────────────────────────

    private async Task<MetricSet?> VerifyAsync(
        int experiment,
        MetricSet reference,
        Uri? baseUrl,
        LoopOptions options,
        string targetDir,
        string resultsPath,
        CancellationToken ct)
    {
        if (options.DryRun)
        {
            return GenerateSyntheticMetrics(reference, experiment);
        }

        LoadTestResult loadResult = await _pipeline.RunLoadTestAsync(
            new LoadTestInput(targetDir, experiment, resultsPath, baseUrl), ct)
            .ConfigureAwait(false);

        if (!loadResult.Success || loadResult.Metrics is null)
        {
            _eventSink.Emit(new StatusMessage(
                $"Load test failed for experiment {experiment}",
                LogLevel.Warning, DateTimeOffset.UtcNow, experiment));
            return null;
        }

        return loadResult.Metrics;
    }

    private static bool HasPersistedBaseline(string targetDir, HoneConfig config)
    {
        string baselineSummaryPath = Path.Combine(
            targetDir, config.Api.ResultsPath, "baseline", "k6-summary.json");
        return File.Exists(baselineSummaryPath);
    }

    private async Task<StartupGateResult> ValidateStartupAsync(
        string targetDir,
        string resultsPath,
        List<ExperimentMetadata> experiments,
        LoopState resumedState,
        RunStateDocument? runState,
        CancellationToken ct)
    {
        if (runState is null)
        {
            return StartupGateResult.Continue;
        }

        if (runState.Status == RecoveryState.RepairRequired)
        {
            return new StartupGateResult(
                CanContinue: false,
                Message: $"Run state at '{_runStateStore.RunStatePath}' is already marked repair_required. Resolve it before starting a new loop.",
                Experiment: runState.CurrentExperiment?.Number,
                UpdatedRunState: null,
                ResumedExperiment: null);
        }

        if (string.IsNullOrWhiteSpace(runState.StableBranch))
        {
            return CreateStartupFailure(runState, "run-state.json is missing stableBranch.");
        }

        if (string.IsNullOrWhiteSpace(runState.StableHeadSha))
        {
            return CreateStartupFailure(
                runState,
                $"run-state.json is missing stableHeadSha for stable branch '{runState.StableBranch}'.");
        }

        if (runState.Status == RecoveryState.Idle &&
            !string.Equals(resumedState.CurrentBranch, runState.StableBranch, StringComparison.Ordinal))
        {
            int lastFinalizedExperiment = experiments.Count == 0 ? 0 : experiments[^1].Experiment;
            string finalizedExperimentLabel = lastFinalizedExperiment == 0
                ? "the current stable baseline"
                : $"last finalized experiment {lastFinalizedExperiment}";

            return CreateStartupFailure(
                runState,
                $"Run metadata expects stable branch '{resumedState.CurrentBranch}' from {finalizedExperimentLabel}, but run-state.json records '{runState.StableBranch}'.");
        }

        bool stableBranchExists = await _versionControl.LocalBranchExistsAsync(
            targetDir,
            runState.StableBranch,
            ct).ConfigureAwait(false);
        if (!stableBranchExists)
        {
            return CreateStartupFailure(
                runState,
                $"Stable branch '{runState.StableBranch}' recorded in run-state.json does not exist locally.");
        }

        OptimizationQueue queueSnapshot = _queueManager.GetSnapshot();
        List<QueueItem> inProgressItems = CollectInProgressItems(queueSnapshot);

        if (runState.Status == RecoveryState.Idle)
        {
            if (runState.CurrentExperiment is not null)
            {
                return CreateStartupFailure(
                    runState,
                    $"run-state.json is idle but still records current experiment {runState.CurrentExperiment.Number}.");
            }

            if (inProgressItems.Count > 0)
            {
                return CreateStartupFailure(
                    runState,
                    $"Optimization queue has in-progress item(s) {DescribeQueueItems(inProgressItems)}, but run-state.json is idle.");
            }

            string currentBranch = await _versionControl.GetCurrentBranchAsync(targetDir, ct)
                .ConfigureAwait(false);
            if (!string.Equals(currentBranch, runState.StableBranch, StringComparison.Ordinal))
            {
                return CreateStartupFailure(
                    runState,
                    $"Current branch '{currentBranch}' does not match stable branch '{runState.StableBranch}' recorded in run-state.json.");
            }

            string headSha = await _versionControl.GetHeadShaAsync(targetDir, ct)
                .ConfigureAwait(false);
            if (!string.Equals(headSha, runState.StableHeadSha, StringComparison.OrdinalIgnoreCase))
            {
                return CreateStartupFailure(
                    runState,
                    $"Current HEAD '{headSha}' does not match stableHeadSha '{runState.StableHeadSha}' recorded in run-state.json.");
            }

            bool isWorkingTreeClean = await IsManagedWorkingTreeCleanAsync(targetDir, resultsPath, ct)
                .ConfigureAwait(false);
            if (!isWorkingTreeClean)
            {
                return CreateStartupFailure(
                    runState,
                    $"Stable branch '{runState.StableBranch}' is dirty. Repair is required before leasing new work.");
            }

            return StartupGateResult.Continue;
        }

        CurrentExperimentState? currentExperiment = runState.CurrentExperiment;
        if (currentExperiment is null)
        {
            return CreateStartupFailure(
                runState,
                $"Run state status '{runState.Status}' is missing currentExperiment.");
        }

        bool metadataAlreadyContainsCurrentExperiment = TryGetExperimentMetadata(
            experiments,
            currentExperiment.Number,
            out ExperimentMetadata? currentExperimentMetadata);

        int expectedExperimentNumber = experiments.Count == 0 ? 1 : experiments[^1].Experiment + 1;
        if (runState.Status == RecoveryState.Finalizing && metadataAlreadyContainsCurrentExperiment)
        {
            expectedExperimentNumber = currentExperiment.Number;
        }

        if (currentExperiment.Number != expectedExperimentNumber)
        {
            return CreateStartupFailure(
                runState,
                $"Current experiment {currentExperiment.Number} does not follow the last finalized experiment {expectedExperimentNumber - 1} from run-metadata.json.");
        }

        string expectedStableBranchFromMetadata =
            runState.Status == RecoveryState.Finalizing &&
            currentExperimentMetadata?.Outcome is ExperimentOutcome.Improved or ExperimentOutcome.EfficiencyWin
                ? currentExperiment.BaseBranch
                : resumedState.CurrentBranch;

        if (!string.Equals(expectedStableBranchFromMetadata, runState.StableBranch, StringComparison.Ordinal))
        {
            int lastFinalizedExperiment = experiments.Count == 0 ? 0 : experiments[^1].Experiment;
            string finalizedExperimentLabel = lastFinalizedExperiment == 0
                ? "the current stable baseline"
                : $"last finalized experiment {lastFinalizedExperiment}";

            return CreateStartupFailure(
                runState,
                $"Run metadata expects stable branch '{expectedStableBranchFromMetadata}' from {finalizedExperimentLabel}, but run-state.json records '{runState.StableBranch}'.");
        }

        if (!string.Equals(currentExperiment.BaseBranch, runState.StableBranch, StringComparison.Ordinal))
        {
            return CreateStartupFailure(
                runState,
                $"Current experiment {currentExperiment.Number} expects base branch '{currentExperiment.BaseBranch}', but run-state.json records stable branch '{runState.StableBranch}'.");
        }

        if (string.IsNullOrWhiteSpace(currentExperiment.BranchName))
        {
            return CreateStartupFailure(
                runState,
                $"Current experiment {currentExperiment.Number} is missing an experiment branch in run-state.json.");
        }

        if (string.IsNullOrWhiteSpace(currentExperiment.QueueItemId))
        {
            return CreateStartupFailure(
                runState,
                $"Current experiment {currentExperiment.Number} is missing a queue item lease in run-state.json.");
        }

        QueueItem? queueItem = FindQueueItem(queueSnapshot, currentExperiment.QueueItemId);
        if (queueItem is null)
        {
            return CreateStartupFailure(
                runState,
                $"Optimization queue is missing leased item '{currentExperiment.QueueItemId}' for experiment {currentExperiment.Number}.");
        }

        if (queueItem.Status == QueueItemStatus.Skipped ||
            (queueItem.Status == QueueItemStatus.Done && runState.Status != RecoveryState.Finalizing))
        {
            return CreateStartupFailure(
                runState,
                $"Optimization queue item '{currentExperiment.QueueItemId}' is already marked {queueItem.Status}, but run-state.json still owns it.");
        }

        foreach (QueueItem inProgressItem in inProgressItems)
        {
            if (!string.Equals(inProgressItem.Id, currentExperiment.QueueItemId, StringComparison.Ordinal))
            {
                return CreateStartupFailure(
                    runState,
                    $"Optimization queue has unrelated in-progress item '{inProgressItem.Id}', but run-state.json owns '{currentExperiment.QueueItemId}'.");
            }
        }

        return runState.Status switch
        {
            RecoveryState.ExperimentLeased or RecoveryState.BranchCreated =>
                await RecoverPreCandidateStateAsync(
                    targetDir,
                    resultsPath,
                    runState,
                    currentExperiment,
                    queueItem,
                    ct).ConfigureAwait(false),

            RecoveryState.CandidateCommitted =>
                await RecoverCandidateCommittedStateAsync(
                    targetDir,
                    resultsPath,
                    runState,
                    currentExperiment,
                    queueItem,
                    ct).ConfigureAwait(false),

            RecoveryState.Finalizing =>
                await RecoverFinalizingStateAsync(
                    targetDir,
                    resultsPath,
                    experiments,
                    runState,
                    currentExperiment,
                    queueItem,
                    ct).ConfigureAwait(false),

            RecoveryState.Idle or RecoveryState.RepairRequired => StartupGateResult.Continue,
            _ => CreateStartupFailure(
                runState,
                $"Run state status '{runState.Status}' is not supported for startup recovery."),
        };
    }

    private async Task<StartupGateResult> RecoverPreCandidateStateAsync(
        string targetDir,
        string resultsPath,
        RunStateDocument runState,
        CurrentExperimentState currentExperiment,
        QueueItem queueItem,
        CancellationToken ct)
    {
        string currentBranch = await _versionControl.GetCurrentBranchAsync(targetDir, ct)
            .ConfigureAwait(false);
        string headSha = await _versionControl.GetHeadShaAsync(targetDir, ct)
            .ConfigureAwait(false);
        bool isWorkingTreeClean = await IsManagedWorkingTreeCleanAsync(targetDir, resultsPath, ct)
            .ConfigureAwait(false);
        if (!isWorkingTreeClean)
        {
            return CreateStartupFailure(
                runState,
                $"Cannot recover experiment {currentExperiment.Number} from state '{runState.Status}' because the working tree is dirty.");
        }

        if (string.Equals(currentBranch, currentExperiment.BranchName, StringComparison.Ordinal) &&
            !string.Equals(headSha, runState.StableHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            _queueManager.MarkInProgress(currentExperiment.QueueItemId, currentExperiment.Number);

            CurrentExperimentState resumedExperiment = currentExperiment with
            {
                Phase = RecoveryState.CandidateCommitted,
                CandidateHeadSha = headSha,
                PendingOutcome = null,
            };

            return StartupGateResult.Resume(
                $"Recovered experiment {currentExperiment.Number} on '{currentExperiment.BranchName}' with candidate commit '{headSha}'. Resuming verification.",
                currentExperiment.Number,
                runState with
                {
                    Status = RecoveryState.CandidateCommitted,
                    CurrentExperiment = resumedExperiment,
                },
                new ResumedExperiment(resumedExperiment, queueItem, runState.StableHeadSha));
        }

        if (string.Equals(currentBranch, currentExperiment.BranchName, StringComparison.Ordinal))
        {
            await _versionControl.CheckoutAsync(targetDir, runState.StableBranch, create: false, ct)
                .ConfigureAwait(false);
            currentBranch = await _versionControl.GetCurrentBranchAsync(targetDir, ct)
                .ConfigureAwait(false);
            headSha = await _versionControl.GetHeadShaAsync(targetDir, ct)
                .ConfigureAwait(false);
            isWorkingTreeClean = await IsManagedWorkingTreeCleanAsync(targetDir, resultsPath, ct)
                .ConfigureAwait(false);

            if (!isWorkingTreeClean)
            {
                return CreateStartupFailure(
                    runState,
                    $"Checked out stable branch '{runState.StableBranch}' while recovering experiment {currentExperiment.Number}, but the working tree is still dirty.");
            }
        }

        if (!string.Equals(currentBranch, runState.StableBranch, StringComparison.Ordinal))
        {
            return CreateStartupFailure(
                runState,
                $"Cannot recover experiment {currentExperiment.Number} from state '{runState.Status}' because current branch '{currentBranch}' is neither stable branch '{runState.StableBranch}' nor experiment branch '{currentExperiment.BranchName}'.");
        }

        if (!string.Equals(headSha, runState.StableHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            return CreateStartupFailure(
                runState,
                $"Cannot recover experiment {currentExperiment.Number} from state '{runState.Status}' because HEAD '{headSha}' does not match stableHeadSha '{runState.StableHeadSha}'.");
        }

        _queueManager.ReleaseLease(currentExperiment.QueueItemId, currentExperiment.Number);
        return StartupGateResult.Recovered(
            $"Recovered experiment {currentExperiment.Number} by releasing queue item '{currentExperiment.QueueItemId}' before a candidate commit was finalized.",
            currentExperiment.Number,
            CreateIdleRunStateDocument(runState.StableBranch, runState.StableHeadSha));
    }

    private async Task<StartupGateResult> RecoverCandidateCommittedStateAsync(
        string targetDir,
        string resultsPath,
        RunStateDocument runState,
        CurrentExperimentState currentExperiment,
        QueueItem queueItem,
        CancellationToken ct)
    {
        string currentBranch = await _versionControl.GetCurrentBranchAsync(targetDir, ct)
            .ConfigureAwait(false);
        bool isWorkingTreeClean = await IsManagedWorkingTreeCleanAsync(targetDir, resultsPath, ct)
            .ConfigureAwait(false);
        if (!isWorkingTreeClean)
        {
            return CreateStartupFailure(
                runState,
                $"Cannot resume candidate_committed experiment {currentExperiment.Number} because the working tree is dirty.");
        }

        if (!string.Equals(currentBranch, currentExperiment.BranchName, StringComparison.Ordinal))
        {
            if (!string.Equals(currentBranch, runState.StableBranch, StringComparison.Ordinal))
            {
                return CreateStartupFailure(
                    runState,
                    $"Cannot resume candidate_committed experiment {currentExperiment.Number} because current branch '{currentBranch}' is neither stable branch '{runState.StableBranch}' nor experiment branch '{currentExperiment.BranchName}'.");
            }

            bool experimentBranchExists = await _versionControl.LocalBranchExistsAsync(
                targetDir,
                currentExperiment.BranchName,
                ct).ConfigureAwait(false);
            if (!experimentBranchExists)
            {
                return CreateStartupFailure(
                    runState,
                    $"Cannot resume candidate_committed experiment {currentExperiment.Number} because branch '{currentExperiment.BranchName}' does not exist locally.");
            }

            await _versionControl.CheckoutAsync(targetDir, currentExperiment.BranchName, create: false, ct)
                .ConfigureAwait(false);
            currentBranch = await _versionControl.GetCurrentBranchAsync(targetDir, ct)
                .ConfigureAwait(false);
        }

        string headSha = await _versionControl.GetHeadShaAsync(targetDir, ct)
            .ConfigureAwait(false);
        isWorkingTreeClean = await IsManagedWorkingTreeCleanAsync(targetDir, resultsPath, ct)
            .ConfigureAwait(false);
        if (!isWorkingTreeClean)
        {
            return CreateStartupFailure(
                runState,
                $"Cannot resume candidate_committed experiment {currentExperiment.Number} because branch '{currentExperiment.BranchName}' is dirty.");
        }

        if (!string.Equals(currentBranch, currentExperiment.BranchName, StringComparison.Ordinal))
        {
            return CreateStartupFailure(
                runState,
                $"Cannot resume candidate_committed experiment {currentExperiment.Number} because current branch '{currentBranch}' does not match '{currentExperiment.BranchName}'.");
        }

        if (string.Equals(headSha, runState.StableHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            return CreateStartupFailure(
                runState,
                $"Cannot resume candidate_committed experiment {currentExperiment.Number} because branch '{currentExperiment.BranchName}' is still at stableHeadSha '{runState.StableHeadSha}'.");
        }

        if (!string.IsNullOrWhiteSpace(currentExperiment.CandidateHeadSha) &&
            !string.Equals(headSha, currentExperiment.CandidateHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            return CreateStartupFailure(
                runState,
                $"Cannot resume candidate_committed experiment {currentExperiment.Number} because branch '{currentExperiment.BranchName}' is at '{headSha}', not recorded candidateHeadSha '{currentExperiment.CandidateHeadSha}'.");
        }

        _queueManager.MarkInProgress(currentExperiment.QueueItemId, currentExperiment.Number);

        CurrentExperimentState resumedExperiment = currentExperiment with
        {
            Phase = RecoveryState.CandidateCommitted,
            CandidateHeadSha = headSha,
            PendingOutcome = null,
        };

        return StartupGateResult.Resume(
            $"Recovered candidate_committed experiment {currentExperiment.Number} on '{currentExperiment.BranchName}'. Resuming verification.",
            currentExperiment.Number,
            runState with
            {
                Status = RecoveryState.CandidateCommitted,
                CurrentExperiment = resumedExperiment,
            },
            new ResumedExperiment(resumedExperiment, queueItem, runState.StableHeadSha));
    }

    private async Task<StartupGateResult> RecoverFinalizingStateAsync(
        string targetDir,
        string resultsPath,
        List<ExperimentMetadata> experiments,
        RunStateDocument runState,
        CurrentExperimentState currentExperiment,
        QueueItem queueItem,
        CancellationToken ct)
    {
        ExperimentMetadata? finalizedExperiment = null;
        int matchCount = 0;

        foreach (ExperimentMetadata experiment in experiments)
        {
            if (experiment.Experiment == currentExperiment.Number)
            {
                finalizedExperiment = experiment;
                matchCount++;
            }
        }

        if (matchCount == 0)
        {
            return CreateStartupFailure(
                runState,
                $"Cannot complete finalizing experiment {currentExperiment.Number} automatically because run-metadata.json does not contain experiment {currentExperiment.Number}.");
        }

        if (matchCount > 1)
        {
            return CreateStartupFailure(
                runState,
                $"Cannot complete finalizing experiment {currentExperiment.Number} automatically because run-metadata.json contains duplicate entries for that experiment.");
        }

        if (currentExperiment.PendingOutcome.HasValue &&
            currentExperiment.PendingOutcome.Value != finalizedExperiment!.Outcome)
        {
            return CreateStartupFailure(
                runState,
                $"Finalizing experiment {currentExperiment.Number} recorded outcome '{currentExperiment.PendingOutcome.Value}', but run-metadata.json records '{finalizedExperiment.Outcome}'.");
        }

        if (queueItem.Status != QueueItemStatus.Done)
        {
            return CreateStartupFailure(
                runState,
                $"Cannot complete finalizing experiment {currentExperiment.Number} automatically because queue item '{currentExperiment.QueueItemId}' is {queueItem.Status} instead of done.");
        }

        string currentBranch = await _versionControl.GetCurrentBranchAsync(targetDir, ct)
            .ConfigureAwait(false);
        string headSha = await _versionControl.GetHeadShaAsync(targetDir, ct)
            .ConfigureAwait(false);
        bool isWorkingTreeClean = await IsManagedWorkingTreeCleanAsync(targetDir, resultsPath, ct)
            .ConfigureAwait(false);
        if (!isWorkingTreeClean)
        {
            return CreateStartupFailure(
                runState,
                $"Cannot complete finalizing experiment {currentExperiment.Number} automatically because the working tree is dirty.");
        }

        ExperimentMetadata? completedExperiment = finalizedExperiment;
        if (completedExperiment?.Outcome is null)
        {
            return CreateStartupFailure(
                runState,
                $"Cannot complete finalizing experiment {currentExperiment.Number} automatically because run-metadata.json does not record an outcome for that experiment.");
        }

        if (IsAcceptedOutcome(completedExperiment.Outcome.Value))
        {
            if (!string.Equals(currentBranch, currentExperiment.BranchName, StringComparison.Ordinal))
            {
                return CreateStartupFailure(
                    runState,
                    $"Accepted finalizing experiment {currentExperiment.Number} must be on branch '{currentExperiment.BranchName}', but current branch is '{currentBranch}'.");
            }

            return StartupGateResult.Recovered(
                $"Recovered finalizing experiment {currentExperiment.Number}; branch '{currentExperiment.BranchName}' is now the stable head.",
                currentExperiment.Number,
                CreateIdleRunStateDocument(currentExperiment.BranchName, headSha));
        }

        if (!string.Equals(currentBranch, runState.StableBranch, StringComparison.Ordinal))
        {
            return CreateStartupFailure(
                runState,
                $"Rejected finalizing experiment {currentExperiment.Number} must be on stable branch '{runState.StableBranch}', but current branch is '{currentBranch}'.");
        }

        if (!string.Equals(headSha, runState.StableHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            return CreateStartupFailure(
                runState,
                $"Rejected finalizing experiment {currentExperiment.Number} expects stableHeadSha '{runState.StableHeadSha}', but current HEAD is '{headSha}'.");
        }

        return StartupGateResult.Recovered(
            $"Recovered rejected finalizing experiment {currentExperiment.Number} on stable branch '{runState.StableBranch}'.",
            currentExperiment.Number,
            CreateIdleRunStateDocument(runState.StableBranch, runState.StableHeadSha));
    }

    private async Task<bool> IsManagedWorkingTreeCleanAsync(
        string targetDir,
        string resultsPath,
        CancellationToken ct)
    {
        if (_versionControl is IPathFilteringVersionControl filteringVersionControl)
        {
            string managedResultsPath = Path.IsPathRooted(resultsPath)
                ? Path.GetRelativePath(targetDir, resultsPath)
                : resultsPath;

            return await filteringVersionControl.IsWorkingTreeCleanAsync(
                targetDir,
                [managedResultsPath],
                ct).ConfigureAwait(false);
        }

        return await _versionControl.IsWorkingTreeCleanAsync(targetDir, ct).ConfigureAwait(false);
    }

    private async Task<RepositoryPreflightResult> PreflightRepositoryAccessAsync(
        string targetDir,
        string resultsPath,
        CancellationToken ct)
    {
        try
        {
            _ = await _versionControl.GetCurrentBranchAsync(targetDir, ct).ConfigureAwait(false);
            _ = await _versionControl.GetHeadShaAsync(targetDir, ct).ConfigureAwait(false);
            _ = await IsManagedWorkingTreeCleanAsync(targetDir, resultsPath, ct).ConfigureAwait(false);

            return RepositoryPreflightResult.Succeeded;
        }
        catch (InvalidOperationException ex)
        {
            return new RepositoryPreflightResult(
                Success: false,
                Message: $"Repository preflight failed: {ex.Message}");
        }
    }

    private LoopResult StopForPreflightFailure(
        string message,
        LoopState state,
        Stopwatch sw)
    {
        _eventSink.Emit(new StatusMessage(
            message,
            LogLevel.Error,
            DateTimeOffset.UtcNow,
            Experiment: null));

        sw.Stop();
        _eventSink.Emit(new PhaseCompleted(
            "loop", sw.Elapsed, Success: false,
            DateTimeOffset.UtcNow, Experiment: null));

        return new LoopResult(
            ExitReason: "preflight_failed",
            ExperimentsRun: 0,
            SuccessCount: state.SuccessCount,
            BestP95: NormalizeBestP95(state.BestP95),
            BestExperiment: state.BestExperiment,
            BaselineP95: double.NaN,
            PrChain: [.. state.PrChain],
            BranchChain: [.. state.BranchChain],
            FailedExperiments: [.. state.FailedExperiments]);
    }

    private async Task<LoopResult> StopForRepairRequiredAsync(
        StartupGateResult startupGate,
        LoopState state,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (startupGate.UpdatedRunState is not null)
        {
            await _runStateStore.SaveAsync(startupGate.UpdatedRunState, ct).ConfigureAwait(false);
        }

        string message = startupGate.UpdatedRunState is null
            ? startupGate.Message
            : $"{startupGate.Message} Run state marked repair_required.";
        _eventSink.Emit(new StatusMessage(
            message,
            LogLevel.Error,
            DateTimeOffset.UtcNow,
            startupGate.Experiment));

        sw.Stop();
        _eventSink.Emit(new PhaseCompleted(
            "loop", sw.Elapsed, Success: false,
            DateTimeOffset.UtcNow, Experiment: null));

        return new LoopResult(
            ExitReason: "repair_required",
            ExperimentsRun: 0,
            SuccessCount: state.SuccessCount,
            BestP95: NormalizeBestP95(state.BestP95),
            BestExperiment: state.BestExperiment,
            BaselineP95: double.NaN,
            PrChain: [.. state.PrChain],
            BranchChain: [.. state.BranchChain],
            FailedExperiments: [.. state.FailedExperiments]);
    }

    // ── Lifecycle helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Best-effort restart of the target API after a failed experiment.
    /// Logs but does not propagate failures — the API must be running for the next experiment.
    /// </summary>
    private async Task TryRestartTargetAsync(
        string targetDir, HoneConfig config, int experiment, CancellationToken ct)
    {
        try
        {
            HookResult result = await _pipeline.StartTargetAsync(targetDir, config, experiment, ct)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                _eventSink.Emit(new StatusMessage(
                    $"Failed to restart target API after failure: {result.Message}",
                    LogLevel.Warning, DateTimeOffset.UtcNow, experiment));
            }
        }
#pragma warning disable CA1031 // Best-effort restart must not crash the loop
        catch (Exception ex)
        {
            _eventSink.Emit(new StatusMessage(
                $"Exception restarting target API: {ex.Message}",
                LogLevel.Warning, DateTimeOffset.UtcNow, experiment));
        }
#pragma warning restore CA1031
    }

    // ── Outcome handlers ────────────────────────────────────────────────────

    private async Task<ExperimentContext> HandleAcceptedAsync(
        ExperimentRunContext ctx,
        QueueItem item,
        MetricSet metrics,
        ComparisonResult comparison,
        CurrentExperimentState currentExperiment,
        CancellationToken ct)
    {
        int exp = ctx.Experiment;
        LoopState state = ctx.State;
        HoneConfig config = ctx.Config;
        string branchName = ctx.BranchName;
        string baseBranch = ctx.BaseBranch;
        string targetDir = ctx.TargetDir;
        CurrentExperimentState finalizingExperiment = currentExperiment with
        {
            Phase = RecoveryState.Finalizing,
            PendingOutcome = comparison.Outcome,
        };

        await SaveRunStateAsync(
            ctx.BaseBranch,
            ctx.ExpectedStableHeadSha,
            RecoveryState.Finalizing,
            finalizingExperiment,
            ct).ConfigureAwait(false);

        // Stage artifacts
        IReadOnlyList<string> artifacts = ArtifactStager.CollectArtifactPaths(
            targetDir, ctx.ResultsPath, exp);

        if (artifacts.Count > 0)
        {
            await _pipeline.CommitArtifactsAsync(
                targetDir, artifacts,
                $"chore: add experiment {exp} artifacts", ct)
                .ConfigureAwait(false);
        }

        // Push and create PR
        _ = await _pipeline.PushBranchAsync(targetDir, branchName, ct)
            .ConfigureAwait(false);

        string prBase = config.Loop.StackedDiffs ? baseBranch : ctx.Options.DefaultBranch;
        PullRequestResult prResult = await _pipeline.CreatePullRequestAsync(
            new CreatePrOptions(
                BaseBranch: prBase,
                HeadBranch: branchName,
                Title: $"perf: experiment {exp} — {item.FilePath}",
                Body: $"P95 improvement: {comparison.ImprovementPct:P1}",
                WorkingDirectory: targetDir), ct)
            .ConfigureAwait(false);

        // Update state
        state.PreviousMetrics = metrics;
        state.PreviousMetricsExperiment = exp;
        double currentP95 = metrics.HttpReqDuration.P95;
        if (currentP95 < state.BestP95)
        {
            state.BestP95 = currentP95;
            state.BestExperiment = exp;
        }

        state.SuccessCount++;
        state.ConsecutiveFailures = 0;
        state.StaleCount = 0;
        state.CurrentBranch = branchName;
        state.BranchChain.Add(branchName);

        if (prResult.PrNumber.HasValue)
        {
            state.PrChain.Add(prResult.PrNumber.Value);
        }

        // Record metadata
        ctx.Experiments.Add(MakeExperimentMetadata(
            exp, ctx.StartedAt, comparison.Outcome, branchName, baseBranch,
            metrics, prResult, state));
        await SaveMetadataAsync(ctx.MetadataPath, ctx.TargetName, ctx.MachineInfo, ctx.Experiments, ct)
            .ConfigureAwait(false);
        _queueManager.MarkDone(item.Id, ToOutcomeName(comparison.Outcome), exp);

        string stableHeadSha = await _versionControl.GetHeadShaAsync(targetDir, ct)
            .ConfigureAwait(false);
        await SaveIdleRunStateAsync(branchName, stableHeadSha, ct)
            .ConfigureAwait(false);

        return CheckExitConditions(state, config)
            ? ExperimentContext.Break
            : ExperimentContext.Continue;
    }

    private async Task<ExperimentContext> HandleFailedExperimentAsync(
        ExperimentRunContext ctx,
        ExperimentOutcome outcome,
        MetricSet? experimentMetrics,
        string? queueItemId,
        CurrentExperimentState currentExperiment,
        CancellationToken ct)
    {
        int exp = ctx.Experiment;
        LoopState state = ctx.State;
        HoneConfig config = ctx.Config;
        string branchName = ctx.BranchName;
        string baseBranch = ctx.BaseBranch;
        string targetDir = ctx.TargetDir;
        CurrentExperimentState finalizingExperiment = currentExperiment with
        {
            Phase = RecoveryState.Finalizing,
            PendingOutcome = outcome,
        };

        await SaveRunStateAsync(
            ctx.BaseBranch,
            ctx.ExpectedStableHeadSha,
            RecoveryState.Finalizing,
            finalizingExperiment,
            ct).ConfigureAwait(false);

        // In stacked-diffs mode, push branch and create rejected PR before reverting
        PullRequestResult? prResult = null;
        if (config.Loop.StackedDiffs)
        {
            _ = await _pipeline.PushBranchAsync(targetDir, branchName, ct)
                .ConfigureAwait(false);
            prResult = await _pipeline.CreatePullRequestAsync(
                new CreatePrOptions(
                    BaseBranch: baseBranch,
                    HeadBranch: branchName,
                    Title: $"perf(rejected): experiment {exp}",
                    Body: $"Rejected: {ToOutcomeName(outcome)}",
                    WorkingDirectory: targetDir), ct)
                .ConfigureAwait(false);
        }

        // Update state before metadata is persisted by the failure handler callback.
        if (outcome == ExperimentOutcome.Stale)
        {
            state.StaleCount++;
        }

        state.ConsecutiveFailures++;
        state.FailedExperiments.Add(exp);

        // Cleanup + queue mark via failure handler
        FailureHandlerResult failureResult = await _failureHandler.HandleFailureAsync(
            new FailureContext(
                BranchName: branchName,
                BaseBranch: baseBranch,
                FilePath: string.Empty,
                Experiment: exp,
                Outcome: ToOutcomeName(outcome),
                RevertDescription: $"Revert {ToOutcomeName(outcome)} experiment {exp}",
                TargetDir: targetDir,
                ExpectedStableHeadSha: ctx.ExpectedStableHeadSha,
                CleanupManifestPath: ctx.CleanupManifestPath,
                KnownUntrackedPaths: [NormalizePath(Path.Combine(ctx.ResultsPath, $"experiment-{exp}"))],
                QueueItemId: queueItemId,
                ResultsPath: ctx.ResultsPath),
            onMetadataUpdate: _ =>
            {
                ctx.Experiments.Add(MakeExperimentMetadata(
                    exp,
                    ctx.StartedAt,
                    outcome,
                    branchName,
                    baseBranch,
                    experimentMetrics,
                    prResult: prResult,
                    state));

                return SaveMetadataAsync(
                    ctx.MetadataPath,
                    ctx.TargetName,
                    ctx.MachineInfo,
                    ctx.Experiments,
                    ct);
            },
            ct).ConfigureAwait(false);

        if (!failureResult.Success)
        {
            state.ExitReason = "cleanup_failed";
            await SaveRunStateAsync(
                ctx.BaseBranch,
                ctx.ExpectedStableHeadSha,
                RecoveryState.RepairRequired,
                finalizingExperiment,
                ct).ConfigureAwait(false);
            _eventSink.Emit(new StatusMessage(
                failureResult.FailureMessage
                    ?? $"Rejected experiment {exp} cleanup failed.",
                LogLevel.Error,
                DateTimeOffset.UtcNow,
                exp));
            return ExperimentContext.Break;
        }

        string stableHeadSha = failureResult.ObservedHeadSha ?? ctx.ExpectedStableHeadSha;
        await SaveIdleRunStateAsync(baseBranch, stableHeadSha, ct)
            .ConfigureAwait(false);

        // Legacy mode: all failures break the loop immediately
        if (!config.Loop.StackedDiffs)
        {
            state.ExitReason = ToOutcomeName(outcome);
            _ = CheckExitConditions(state, config);
            return ExperimentContext.Break;
        }

        return CheckExitConditions(state, config)
            ? ExperimentContext.Break
            : ExperimentContext.Continue;
    }

    // ── State helpers ───────────────────────────────────────────────────────

    private static CurrentExperimentState CreateLeasedExperimentState(
        ExperimentRunContext ctx,
        QueueItem item) =>
        new()
        {
            Number = ctx.Experiment,
            QueueItemId = item.Id,
            BranchName = ctx.BranchName,
            BaseBranch = ctx.BaseBranch,
            CleanupManifestPath = ctx.CleanupManifestPath,
            Phase = RecoveryState.ExperimentLeased,
            StartedAt = ctx.StartedAt.ToString("o"),
        };

    private async Task<CurrentExperimentState> PersistObservedPhaseAsync(
        ExperimentRunContext ctx,
        CurrentExperimentState currentExperiment,
        string? candidateHeadShaHint,
        CancellationToken ct)
    {
        string currentBranch = await _versionControl.GetCurrentBranchAsync(ctx.TargetDir, ct)
            .ConfigureAwait(false);
        string headSha = await _versionControl.GetHeadShaAsync(ctx.TargetDir, ct)
            .ConfigureAwait(false);

        RecoveryState phase;
        string? candidateHeadSha;

        if (!string.IsNullOrWhiteSpace(candidateHeadShaHint))
        {
            phase = RecoveryState.CandidateCommitted;
            candidateHeadSha = candidateHeadShaHint;
        }
        else if (string.Equals(currentBranch, currentExperiment.BranchName, StringComparison.Ordinal))
        {
            bool matchesStableHead = string.Equals(
                headSha,
                ctx.ExpectedStableHeadSha,
                StringComparison.OrdinalIgnoreCase);

            phase = matchesStableHead
                ? RecoveryState.BranchCreated
                : RecoveryState.CandidateCommitted;
            candidateHeadSha = matchesStableHead ? null : headSha;
        }
        else
        {
            phase = RecoveryState.ExperimentLeased;
            candidateHeadSha = null;
        }

        CurrentExperimentState updatedExperiment = currentExperiment with
        {
            Phase = phase,
            CandidateHeadSha = candidateHeadSha,
            PendingOutcome = null,
        };

        await SaveRunStateAsync(
            ctx.BaseBranch,
            ctx.ExpectedStableHeadSha,
            phase,
            updatedExperiment,
            ct).ConfigureAwait(false);

        return updatedExperiment;
    }

    private Task SaveIdleRunStateAsync(
        string stableBranch,
        string stableHeadSha,
        CancellationToken ct) =>
        SaveRunStateAsync(
            stableBranch: stableBranch,
            stableHeadSha: stableHeadSha,
            status: RecoveryState.Idle,
            currentExperiment: null,
            ct: ct);

    private Task SaveRunStateAsync(
        string stableBranch,
        string stableHeadSha,
        RecoveryState status,
        CurrentExperimentState? currentExperiment,
        CancellationToken ct) =>
        _runStateStore.SaveAsync(
            new RunStateDocument
            {
                StableBranch = stableBranch,
                StableHeadSha = stableHeadSha,
                Status = status,
                CurrentExperiment = currentExperiment,
            },
            ct);

    private static RunStateDocument CreateIdleRunStateDocument(
        string stableBranch,
        string stableHeadSha) =>
        new()
        {
            StableBranch = stableBranch,
            StableHeadSha = stableHeadSha,
            Status = RecoveryState.Idle,
        };

    private static LoopState CreateResumedState(
        string defaultBranch,
        List<ExperimentMetadata> experiments,
        double initialBestP95)
    {
        var state = new LoopState
        {
            BestP95 = initialBestP95,
            CurrentBranch = defaultBranch,
        };

        ResumeState(state, experiments);
        return state;
    }

    private static StartupGateResult CreateStartupFailure(
        RunStateDocument runState,
        string message) =>
        new(
            CanContinue: false,
            Message: message,
            Experiment: runState.CurrentExperiment?.Number,
            UpdatedRunState: runState with { Status = RecoveryState.RepairRequired },
            ResumedExperiment: null);

    private static double NormalizeBestP95(double bestP95) =>
        double.IsPositiveInfinity(bestP95) ? double.NaN : bestP95;

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');

    private static DateTimeOffset ParseStartedAt(string startedAt) =>
        DateTimeOffset.TryParse(
            startedAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset parsedStartedAt)
            ? parsedStartedAt
            : DateTimeOffset.UtcNow;

    private static QueueItem? FindQueueItem(OptimizationQueue queueSnapshot, string itemId)
    {
        foreach (QueueItem item in queueSnapshot.Items)
        {
            if (string.Equals(item.Id, itemId, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }

    private static bool TryGetExperimentMetadata(
        IReadOnlyList<ExperimentMetadata> experiments,
        int experimentNumber,
        out ExperimentMetadata? metadata)
    {
        foreach (ExperimentMetadata experiment in experiments)
        {
            if (experiment.Experiment == experimentNumber)
            {
                metadata = experiment;
                return true;
            }
        }

        metadata = null;
        return false;
    }

    private static List<QueueItem> CollectInProgressItems(OptimizationQueue queueSnapshot)
    {
        List<QueueItem> inProgressItems = [];

        foreach (QueueItem item in queueSnapshot.Items)
        {
            if (item.Status == QueueItemStatus.InProgress)
            {
                inProgressItems.Add(item);
            }
        }

        return inProgressItems;
    }

    private static bool IsAcceptedOutcome(ExperimentOutcome outcome) =>
        outcome is ExperimentOutcome.Improved or ExperimentOutcome.EfficiencyWin;

    private static string DescribeQueueItems(List<QueueItem> items)
    {
        if (items.Count == 0)
        {
            return "none";
        }

        string[] labels = new string[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            labels[i] = $"#{items[i].Id}";
        }

        return string.Join(", ", labels);
    }

    private static void ResumeState(
        LoopState state,
        IReadOnlyList<ExperimentMetadata> experiments)
    {
        foreach (ExperimentMetadata meta in experiments)
        {
            if (meta.Outcome is ExperimentOutcome.Improved or ExperimentOutcome.EfficiencyWin)
            {
                state.SuccessCount++;
                state.ConsecutiveFailures = 0;
                state.StaleCount = 0;

                if (meta.P95.HasValue && meta.P95.Value < state.BestP95)
                {
                    state.BestP95 = meta.P95.Value;
                    state.BestExperiment = meta.Experiment;
                }

                if (!string.IsNullOrEmpty(meta.BranchName))
                {
                    state.CurrentBranch = meta.BranchName;
                    state.BranchChain.Add(meta.BranchName);
                }

                if (meta.PrNumber.HasValue)
                {
                    state.PrChain.Add(meta.PrNumber.Value);
                }
            }
            else
            {
                state.ConsecutiveFailures++;
                state.FailedExperiments.Add(meta.Experiment);

                if (meta.Outcome == ExperimentOutcome.Stale)
                {
                    state.StaleCount++;
                }
            }
        }
    }

    private static bool CheckExitConditions(LoopState state, HoneConfig config)
    {
        if (state.StaleCount >= config.Tolerances.StaleExperimentsBeforeStop)
        {
            state.ExitReason = "stale_limit";
            return true;
        }

        if (state.ConsecutiveFailures >= config.Tolerances.MaxConsecutiveFailures)
        {
            state.ExitReason = "max_consecutive_failures";
            return true;
        }

        return false;
    }

    // ── Synthetic metrics (DryRun) ──────────────────────────────────────────

    internal static MetricSet GenerateSyntheticMetrics(MetricSet reference, int experiment)
    {
        const double ImprovementFactor = 0.95;
        HttpReqDurationMetrics refDuration = reference.HttpReqDuration;

        return new MetricSet(
            Timestamp: DateTimeOffset.UtcNow.ToString("o"),
            Experiment: experiment,
            Run: 1,
            HttpReqDuration: new HttpReqDurationMetrics(
                Avg: refDuration.Avg * ImprovementFactor,
                P50: refDuration.P50 * ImprovementFactor,
                P90: refDuration.P90 * ImprovementFactor,
                P95: refDuration.P95 * ImprovementFactor,
                P99: refDuration.P99 * ImprovementFactor,
                Max: refDuration.Max * ImprovementFactor),
            HttpReqs: new HttpReqCountMetrics(
                Count: reference.HttpReqs.Count,
                Rate: reference.HttpReqs.Rate * 1.05),
            HttpReqFailed: new HttpReqFailedMetrics(Count: 0, Rate: 0),
            SummaryPath: null);
    }

    // ── Metadata helpers ────────────────────────────────────────────────────

    private static ExperimentMetadata MakeExperimentMetadata(
        int experiment,
        DateTimeOffset startedAt,
        ExperimentOutcome outcome,
        string branchName,
        string baseBranch,
        MetricSet? metrics,
        PullRequestResult? prResult,
        LoopState state) =>
        new(
            Experiment: experiment,
            StartedAt: startedAt.ToString("o"),
            CompletedAt: DateTimeOffset.UtcNow.ToString("o"),
            Outcome: outcome,
            BranchName: branchName,
            BaseBranch: baseBranch,
            P95: metrics?.HttpReqDuration.P95,
            RPS: metrics?.HttpReqs.Rate,
            PrNumber: prResult?.PrNumber,
            PrUrl: prResult?.PrUrl,
            StaleCount: state.StaleCount,
            ConsecutiveFailures: state.ConsecutiveFailures);

    private static ExperimentOutcome MapImplementerFailureOutcome(IterativeFixResult result) =>
        result.ExitReason switch
        {
            "build_failure" => ExperimentOutcome.BuildFailed,
            "test_failure" => ExperimentOutcome.TestFailed,
            "retry_budget_exhausted" => MapRetryBudgetExhaustionOutcome(result.IterationLog),
            _ => ExperimentOutcome.ImplementationFailed,
        };

    private static ExperimentOutcome MapRetryBudgetExhaustionOutcome(
        IterationLog? iterationLog)
    {
        IReadOnlyList<IterationAttempt>? attempts = iterationLog?.Attempts;
        if (attempts is null || attempts.Count == 0)
        {
            return ExperimentOutcome.ImplementationFailed;
        }

        string? stage = attempts[^1].Stage;
        return stage switch
        {
            "build" => ExperimentOutcome.BuildFailed,
            "test" => ExperimentOutcome.TestFailed,
            _ => ExperimentOutcome.ImplementationFailed,
        };
    }

    private static string ToOutcomeName(ExperimentOutcome outcome) =>
        outcome switch
        {
            ExperimentOutcome.Improved => "improved",
            ExperimentOutcome.Regressed => "regressed",
            ExperimentOutcome.Stale => "stale",
            ExperimentOutcome.EfficiencyWin => "efficiency_win",
            ExperimentOutcome.ImplementationFailed => "implementation_failed",
            ExperimentOutcome.BuildFailed => "build_failed",
            ExperimentOutcome.TestFailed => "test_failed",
            ExperimentOutcome.StartFailed => "start_failed",
            ExperimentOutcome.LoadTestFailed => "load_test_failed",
            ExperimentOutcome.Unknown or _ => "unknown",
        };

    private async Task SaveMetadataAsync(
        string metadataPath,
        string targetName,
        MachineInfo machineInfo,
        List<ExperimentMetadata> experiments,
        CancellationToken ct)
    {
        var metadata = new RunMetadata(
            TargetName: targetName,
            StartedAt: experiments.Count > 0 ? experiments[0].StartedAt : DateTimeOffset.UtcNow.ToString("o"),
            MachineInfo: machineInfo,
            Experiments: experiments);

        await _pipeline.SaveRunMetadataAsync(metadataPath, metadata, ct)
            .ConfigureAwait(false);
    }

    // ── Helper types ────────────────────────────────────────────────────────

    /// <summary>Signals whether the loop should continue or break after an experiment.</summary>
    private readonly record struct ExperimentContext(bool ShouldBreak, bool TargetLifecycleTakenOver)
    {
        internal static ExperimentContext Break { get; } = new(ShouldBreak: true, TargetLifecycleTakenOver: false);
        internal static ExperimentContext Continue { get; } = new(ShouldBreak: false, TargetLifecycleTakenOver: false);
    }

    private sealed record ResumedExperiment(
        CurrentExperimentState CurrentExperiment,
        QueueItem QueueItem,
        string StableHeadSha);

    private readonly record struct RepositoryPreflightResult(
        bool Success,
        string Message)
    {
        internal static RepositoryPreflightResult Succeeded { get; } = new(
            Success: true,
            Message: string.Empty);
    }

    private sealed record StartupGateResult(
        bool CanContinue,
        string Message,
        int? Experiment,
        RunStateDocument? UpdatedRunState,
        ResumedExperiment? ResumedExperiment)
    {
        internal static StartupGateResult Continue { get; } = new(
            CanContinue: true,
            Message: string.Empty,
            Experiment: null,
            UpdatedRunState: null,
            ResumedExperiment: null);

        internal static StartupGateResult Recovered(
            string message,
            int experiment,
            RunStateDocument updatedRunState) =>
            new(
                CanContinue: true,
                Message: message,
                Experiment: experiment,
                UpdatedRunState: updatedRunState,
                ResumedExperiment: null);

        internal static StartupGateResult Resume(
            string message,
            int experiment,
            RunStateDocument updatedRunState,
            ResumedExperiment resumedExperiment) =>
            new(
                CanContinue: true,
                Message: message,
                Experiment: experiment,
                UpdatedRunState: updatedRunState,
                ResumedExperiment: resumedExperiment);
    }
}
