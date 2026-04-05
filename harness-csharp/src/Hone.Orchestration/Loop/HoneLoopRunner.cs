using System.Diagnostics;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Orchestration.Artifacts;
using Hone.Orchestration.Failure;
using Hone.Orchestration.Implementer;
using Hone.Orchestration.Queue;

namespace Hone.Orchestration.Loop;

/// <summary>
/// Main entry point for the Hone optimization loop.
/// Orchestrates analysis → queue → implement → verify → accept/reject for each experiment.
/// Mirrors <c>harness/Invoke-HoneLoop.ps1</c>.
/// </summary>
internal sealed class HoneLoopRunner
{
    private readonly ILoopPipeline _pipeline;
    private readonly OptimizationQueueManager _queueManager;
    private readonly IterativeImplementerRunner _implementer;
    private readonly ExperimentFailureHandler _failureHandler;
    private readonly IHoneEventSink _eventSink;

    internal HoneLoopRunner(
        ILoopPipeline pipeline,
        OptimizationQueueManager queueManager,
        IterativeImplementerRunner implementer,
        ExperimentFailureHandler failureHandler,
        IHoneEventSink eventSink)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(queueManager);
        ArgumentNullException.ThrowIfNull(implementer);
        ArgumentNullException.ThrowIfNull(failureHandler);
        ArgumentNullException.ThrowIfNull(eventSink);

        _pipeline = pipeline;
        _queueManager = queueManager;
        _implementer = implementer;
        _failureHandler = failureHandler;
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

        // ── Phase 1: Baseline ───────────────────────────────────────────────
        MetricSet baseline = await _pipeline.LoadOrCreateBaselineAsync(targetDir, config, ct)
            .ConfigureAwait(false);
        double baselineP95 = baseline.HttpReqDuration.P95;

        // ── Phase 2: Machine info ───────────────────────────────────────────
        MachineInfo machineInfo = await _pipeline.GetMachineInfoAsync(ct)
            .ConfigureAwait(false);

        // ── Phase 3: Load or initialise run metadata ────────────────────────
        RunMetadata? existingMetadata = await _pipeline.LoadRunMetadataAsync(metadataPath, ct)
            .ConfigureAwait(false);
        string targetName = options.TargetName
            ?? existingMetadata?.TargetName
            ?? "unknown";
        var experiments = new List<ExperimentMetadata>(existingMetadata?.Experiments ?? []);
        int priorCount = experiments.Count;

        // ── Phase 4: Resume state from prior experiments ────────────────────
        var state = new LoopState
        {
            BestP95 = baselineP95,
            CurrentBranch = options.DefaultBranch,
        };
        ResumeState(state, experiments);

        // ── Phase 5: Experiment loop ────────────────────────────────────────
        int maxExp = options.MaxExperimentsOverride ?? config.Loop.MaxExperiments;
        int startExperiment = experiments.Count + 1;

        for (int exp = startExperiment; exp < startExperiment + maxExp; exp++)
        {
            ct.ThrowIfCancellationRequested();

            ExperimentContext expCtx = await RunSingleExperimentAsync(
                exp, state, baseline, options, config, targetDir, resultsPath,
                targetName, machineInfo, experiments, metadataPath, ct)
                .ConfigureAwait(false);

            if (expCtx.ShouldBreak)
            {
                break;
            }
        }

        // ── Phase 6: Finalise ───────────────────────────────────────────────
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
        string startedAt = DateTimeOffset.UtcNow.ToString("o");
        _eventSink.Emit(new PhaseStarted("experiment", DateTimeOffset.UtcNow, exp));

        MetricSet reference = state.PreviousMetrics ?? baseline;
        string branchName = $"{config.Loop.BranchPrefix}-{exp}";
        string baseBranch = state.CurrentBranch;

        // ── Analyse (if queue empty) ────────────────────────────────────────
        if (!_queueManager.HasActionable())
        {
            bool analysisOk = await TryAnalyseAsync(exp, baseline, state, targetDir, ct)
                .ConfigureAwait(false);
            if (!analysisOk)
            {
                state.ExitReason = "no_opportunities";
                return ExperimentContext.Break;
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
                return ExperimentContext.Break;
            }

            if (config.Loop.SkipClassification)
            {
                break;
            }

            ClassificationResult classResult = await _pipeline.ClassifyAsync(
                new ClassificationInput(item.FilePath, item.Explanation, exp, targetDir), ct)
                .ConfigureAwait(false);

            if (classResult.Success && classResult.Scope == OpportunityScope.Architecture)
            {
                _queueManager.MarkDone(item.Id, "skipped_architecture", exp);
                continue;
            }

            break;
        }

        // ── Implement ───────────────────────────────────────────────────────
        ImplementerRunResult implResult = await _implementer.RunAsync(
            new ImplementerOptions(
                FilePath: item.FilePath,
                Explanation: item.Explanation,
                RootCauseDocument: null,
                Experiment: exp,
                BaseBranch: baseBranch,
                TargetDir: targetDir,
                TargetName: targetName,
                Config: config.Implementer,
                TestProjectPaths: null,
                BranchPrefix: config.Loop.BranchPrefix,
                ResultsPath: resultsPath), ct)
            .ConfigureAwait(false);

        if (!implResult.Result.Success)
        {
            return await HandleImplementationFailureAsync(
                exp, startedAt, branchName, baseBranch, item, implResult,
                state, config, targetDir, targetName, machineInfo,
                experiments, metadataPath, ct)
                .ConfigureAwait(false);
        }

        // ── Verify ──────────────────────────────────────────────────────────
        MetricSet? experimentMetrics = await VerifyAsync(
            exp, reference, options, targetDir, resultsPath, ct)
            .ConfigureAwait(false);

        if (experimentMetrics is null)
        {
            return await HandleVerificationFailureAsync(
                exp, startedAt, branchName, baseBranch, item,
                state, config, targetDir, targetName, machineInfo,
                experiments, metadataPath, ct)
                .ConfigureAwait(false);
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
                await HandleAcceptedAsync(
                    exp, startedAt, branchName, baseBranch, item,
                    experimentMetrics, comparison, state, config, options,
                    targetDir, resultsPath, targetName, machineInfo,
                    experiments, metadataPath, ct).ConfigureAwait(false),

            ExperimentOutcome.Regressed =>
                await HandleRejectedAsync(
                    exp, startedAt, branchName, baseBranch, item,
                    experimentMetrics, comparison, state, config,
                    targetDir, targetName, machineInfo,
                    experiments, metadataPath, "regressed", ct).ConfigureAwait(false),

            ExperimentOutcome.Stale =>
                await HandleRejectedAsync(
                    exp, startedAt, branchName, baseBranch, item,
                    experimentMetrics, comparison, state, config,
                    targetDir, targetName, machineInfo,
                    experiments, metadataPath, "stale", ct).ConfigureAwait(false),

            _ =>
                await HandleRejectedAsync(
                    exp, startedAt, branchName, baseBranch, item,
                    experimentMetrics, comparison, state, config,
                    targetDir, targetName, machineInfo,
                    experiments, metadataPath, "stale", ct).ConfigureAwait(false),
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
            new LoadTestInput(targetDir, experiment, resultsPath), ct)
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

    // ── Outcome handlers ────────────────────────────────────────────────────

    private async Task<ExperimentContext> HandleAcceptedAsync(
        int exp,
        string startedAt,
        string branchName,
        string baseBranch,
        QueueItem item,
        MetricSet metrics,
        ComparisonResult comparison,
        LoopState state,
        HoneConfig config,
        LoopOptions options,
        string targetDir,
        string resultsPath,
        string targetName,
        MachineInfo machineInfo,
        List<ExperimentMetadata> experiments,
        string metadataPath,
        CancellationToken ct)
    {
        // Stage artifacts
        IReadOnlyList<string> artifacts = ArtifactStager.CollectArtifactPaths(
            targetDir, resultsPath, exp);

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

        string prBase = config.Loop.StackedDiffs ? baseBranch : options.DefaultBranch;
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

        string outcomeName = comparison.Outcome switch
        {
            ExperimentOutcome.Improved => "improved",
            ExperimentOutcome.EfficiencyWin => "efficiency_win",
            ExperimentOutcome.Regressed => "regressed",
            ExperimentOutcome.Stale => "stale",
            _ => "stale",
        };
        _queueManager.MarkDone(item.Id, outcomeName, exp);

        // Record metadata
        experiments.Add(MakeExperimentMetadata(
            exp, startedAt, comparison.Outcome, branchName, baseBranch,
            metrics, prResult, state));
        await SaveMetadataAsync(metadataPath, targetName, machineInfo, experiments, ct)
            .ConfigureAwait(false);

        return CheckExitConditions(state, config)
            ? ExperimentContext.Break
            : ExperimentContext.Continue;
    }

    private async Task<ExperimentContext> HandleRejectedAsync(
        int exp,
        string startedAt,
        string branchName,
        string baseBranch,
        QueueItem item,
        MetricSet? metrics,
        ComparisonResult comparison,
        LoopState state,
        HoneConfig config,
        string targetDir,
        string targetName,
        MachineInfo machineInfo,
        List<ExperimentMetadata> experiments,
        string metadataPath,
        string outcome,
        CancellationToken ct)
    {
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
                    Title: $"perf(rejected): experiment {exp} — {item.FilePath}",
                    Body: $"Rejected: {outcome}",
                    WorkingDirectory: targetDir), ct)
                .ConfigureAwait(false);
        }

        // Revert + queue mark via failure handler
        _ = await _failureHandler.HandleFailureAsync(
            new FailureContext(
                BranchName: branchName,
                FilePath: item.FilePath,
                Experiment: exp,
                Outcome: outcome,
                RevertDescription: $"Revert {outcome} experiment {exp}",
                TargetDir: targetDir,
                QueueItemId: item.Id),
            onMetadataUpdate: null,
            ct).ConfigureAwait(false);

        await _pipeline.CheckoutAsync(targetDir, baseBranch, ct)
            .ConfigureAwait(false);

        // Update state
        if (string.Equals(outcome, "stale", StringComparison.Ordinal))
        {
            state.StaleCount++;
        }

        state.ConsecutiveFailures++;
        state.FailedExperiments.Add(exp);

        // Record metadata
        experiments.Add(MakeExperimentMetadata(
            exp, startedAt, comparison.Outcome, branchName, baseBranch,
            metrics, prResult: prResult, state));
        await SaveMetadataAsync(metadataPath, targetName, machineInfo, experiments, ct)
            .ConfigureAwait(false);

        // Legacy mode: all failures break the loop immediately
        if (!config.Loop.StackedDiffs)
        {
            if (string.Equals(outcome, "regressed", StringComparison.Ordinal))
            {
                state.ExitReason = "regression";
            }
            else
            {
                _ = CheckExitConditions(state, config);
            }

            return ExperimentContext.Break;
        }

        return CheckExitConditions(state, config)
            ? ExperimentContext.Break
            : ExperimentContext.Continue;
    }

    private async Task<ExperimentContext> HandleImplementationFailureAsync(
        int exp,
        string startedAt,
        string branchName,
        string baseBranch,
        QueueItem item,
        ImplementerRunResult implResult,
        LoopState state,
        HoneConfig config,
        string targetDir,
        string targetName,
        MachineInfo machineInfo,
        List<ExperimentMetadata> experiments,
        string metadataPath,
        CancellationToken ct)
    {
        _ = await _failureHandler.HandleFailureAsync(
            new FailureContext(
                BranchName: branchName,
                FilePath: item.FilePath,
                Experiment: exp,
                Outcome: implResult.Result.ExitReason,
                RevertDescription: $"Revert failed implementation for experiment {exp}",
                TargetDir: targetDir,
                QueueItemId: item.Id),
            onMetadataUpdate: null,
            ct).ConfigureAwait(false);

        await _pipeline.CheckoutAsync(targetDir, baseBranch, ct)
            .ConfigureAwait(false);

        state.ConsecutiveFailures++;
        state.FailedExperiments.Add(exp);

        experiments.Add(MakeFailedExperimentMetadata(
            exp, startedAt, branchName, baseBranch, state));
        await SaveMetadataAsync(metadataPath, targetName, machineInfo, experiments, ct)
            .ConfigureAwait(false);

        // Legacy mode: all failures break the loop immediately
        if (!config.Loop.StackedDiffs)
        {
            state.ExitReason = implResult.Result.ExitReason ?? "implementation_failed";
            return ExperimentContext.Break;
        }

        return CheckExitConditions(state, config)
            ? ExperimentContext.Break
            : ExperimentContext.Continue;
    }

    private async Task<ExperimentContext> HandleVerificationFailureAsync(
        int exp,
        string startedAt,
        string branchName,
        string baseBranch,
        QueueItem item,
        LoopState state,
        HoneConfig config,
        string targetDir,
        string targetName,
        MachineInfo machineInfo,
        List<ExperimentMetadata> experiments,
        string metadataPath,
        CancellationToken ct)
    {
        _ = await _failureHandler.HandleFailureAsync(
            new FailureContext(
                BranchName: branchName,
                FilePath: item.FilePath,
                Experiment: exp,
                Outcome: "load_test_failed",
                RevertDescription: $"Revert experiment {exp} after load test failure",
                TargetDir: targetDir,
                QueueItemId: item.Id),
            onMetadataUpdate: null,
            ct).ConfigureAwait(false);

        await _pipeline.CheckoutAsync(targetDir, baseBranch, ct)
            .ConfigureAwait(false);

        state.ConsecutiveFailures++;
        state.FailedExperiments.Add(exp);

        experiments.Add(MakeFailedExperimentMetadata(
            exp, startedAt, branchName, baseBranch, state));
        await SaveMetadataAsync(metadataPath, targetName, machineInfo, experiments, ct)
            .ConfigureAwait(false);

        // Legacy mode: all failures break the loop immediately
        if (!config.Loop.StackedDiffs)
        {
            state.ExitReason = "load_test_failed";
            return ExperimentContext.Break;
        }

        return CheckExitConditions(state, config)
            ? ExperimentContext.Break
            : ExperimentContext.Continue;
    }

    // ── State helpers ───────────────────────────────────────────────────────

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
        string startedAt,
        ExperimentOutcome outcome,
        string branchName,
        string baseBranch,
        MetricSet? metrics,
        PullRequestResult? prResult,
        LoopState state) =>
        new(
            Experiment: experiment,
            StartedAt: startedAt,
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

    private static ExperimentMetadata MakeFailedExperimentMetadata(
        int experiment,
        string startedAt,
        string branchName,
        string baseBranch,
        LoopState state) =>
        new(
            Experiment: experiment,
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.UtcNow.ToString("o"),
            Outcome: null,
            BranchName: branchName,
            BaseBranch: baseBranch,
            P95: null,
            RPS: null,
            PrNumber: null,
            PrUrl: null,
            StaleCount: state.StaleCount,
            ConsecutiveFailures: state.ConsecutiveFailures);

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
    private readonly record struct ExperimentContext(bool ShouldBreak)
    {
        internal static ExperimentContext Break { get; } = new(ShouldBreak: true);
        internal static ExperimentContext Continue { get; } = new(ShouldBreak: false);
    }
}
