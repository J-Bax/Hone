using FluentAssertions;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Orchestration.Implementer;
using Hone.Orchestration.Loop;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Integration.Tests;

public sealed class HoneLoopIntegrationTests(ITestOutputHelper output)
    : IntegrationTestBase(output)
{
    // ── 1. HappyPath_SingleExperiment ────────────────────────────────────────

    [Fact]
    public async Task HappyPath_SingleExperiment()
    {
        // Arrange: analysis returns 1 opportunity, implementation succeeds,
        // load test shows improvement → PR created.
        TestHarness h = CreateHarness(pipeline =>
        {
            _ = pipeline.RunAnalysisAsync(
                    Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AnalysisResult(
                    Success: true, Opportunities: MakeOpportunities(count: 1))));
            _ = pipeline.RunLoadTestAsync(
                    Arg.Any<LoadTestInput>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new LoadTestResult(
                    Success: true, Metrics: MakeImprovedMetrics(experiment: 1),
                    SummaryPath: null, Output: null)));
            _ = pipeline.CompareMetrics(
                    Arg.Any<MetricSet>(), Arg.Any<MetricSet>(), Arg.Any<MetricSet?>(),
                    Arg.Any<int>(), Arg.Any<HoneConfig>())
                .Returns(ImprovedComparison());
        });

        // Act
        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 1));

        // Assert
        _ = result.ExitReason.Should().Be("max_experiments");
        _ = result.ExperimentsRun.Should().Be(1);
        _ = result.SuccessCount.Should().Be(1);
        _ = result.BestP95.Should().BeLessThan(BaselineMetrics.HttpReqDuration.P95);
        _ = result.BaselineP95.Should().Be(BaselineMetrics.HttpReqDuration.P95);
        _ = result.PrChain.Should().HaveCount(1);
        _ = result.BranchChain.Should().ContainSingle()
            .Which.Should().Be("hone/experiment-1");
        _ = result.FailedExperiments.Should().BeEmpty();

        // Verify pipeline interactions
        _ = await h.Pipeline.Received(1).PushBranchAsync(
            Arg.Any<string>(), Arg.Is("hone/experiment-1"), Arg.Any<CancellationToken>());
        _ = await h.Pipeline.Received(1).CreatePullRequestAsync(
            Arg.Any<CreatePrOptions>(), Arg.Any<CancellationToken>());
    }

    // ── 2. BuildFailure_ExperimentRejected ───────────────────────────────────

    [Fact]
    public async Task BuildFailure_ExperimentRejected()
    {
        // Arrange: analysis returns opportunity, build step fails →
        // experiment recorded as failure, loop continues in stacked-diffs.
        TestHarness h = CreateHarness(
            configurePipeline: pipeline =>
                _ = pipeline.RunAnalysisAsync(
                        Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new AnalysisResult(
                        Success: true, Opportunities: MakeOpportunities(count: 2)))),
            configureImplementer: implPipeline =>
                _ = implPipeline.BuildProjectAsync(
                        Arg.Any<BuildStepInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new BuildStepResult(Success: false, Output: "Build error CS0001"))));

        // Act — 2 experiments, both will fail at the build step
        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 2));

        // Assert
        _ = result.ExperimentsRun.Should().Be(2);
        _ = result.SuccessCount.Should().Be(0);
        _ = result.FailedExperiments.Should().HaveCount(2);

        // Failure handler should have reverted for each failed experiment
        await h.VersionControl.Received(2).RevertLastCommitAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── 3. TestFailure_ExperimentRejected ────────────────────────────────────

    [Fact]
    public async Task TestFailure_ExperimentRejected()
    {
        // Arrange: build succeeds but tests fail
        TestHarness h = CreateHarness(
            configurePipeline: pipeline =>
                _ = pipeline.RunAnalysisAsync(
                        Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new AnalysisResult(
                        Success: true, Opportunities: MakeOpportunities(count: 2)))),
            configureImplementer: implPipeline =>
                _ = implPipeline.RunTestsAsync(
                        Arg.Any<TestStepInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new TestStepResult(Success: false, Output: "Test failure"))));

        // Act
        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 2));

        // Assert
        _ = result.ExperimentsRun.Should().Be(2);
        _ = result.SuccessCount.Should().Be(0);
        _ = result.FailedExperiments.Should().HaveCount(2);

        await h.VersionControl.Received(2).RevertLastCommitAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── 4. PerfRegression_ExperimentRejected ─────────────────────────────────

    [Fact]
    public async Task PerfRegression_ExperimentRejected()
    {
        // Arrange: implementation succeeds, load test succeeds, but
        // CompareMetrics returns Regressed → revert, PR created with rejection,
        // loop continues in stacked-diffs mode.
        TestHarness h = CreateHarness(configurePipeline: pipeline =>
        {
            _ = pipeline.RunAnalysisAsync(
                    Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AnalysisResult(
                    Success: true, Opportunities: MakeOpportunities(count: 2))));
            _ = pipeline.RunLoadTestAsync(
                    Arg.Any<LoadTestInput>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult(new LoadTestResult(
                    Success: true,
                    Metrics: MakeImprovedMetrics(experiment: callInfo.Arg<LoadTestInput>().Experiment),
                    SummaryPath: null, Output: null)));
            _ = pipeline.CompareMetrics(
                    Arg.Any<MetricSet>(), Arg.Any<MetricSet>(), Arg.Any<MetricSet?>(),
                    Arg.Any<int>(), Arg.Any<HoneConfig>())
                .Returns(RegressedComparison());
        });

        // Act
        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 2));

        // Assert — both experiments regressed and were reverted
        _ = result.ExperimentsRun.Should().Be(2);
        _ = result.SuccessCount.Should().Be(0);
        _ = result.FailedExperiments.Should().HaveCount(2);

        // Revert should have been called for each regressed experiment
        await h.VersionControl.Received(2).RevertLastCommitAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Stacked-diffs mode creates rejected PRs
        _ = await h.Pipeline.Received(2).CreatePullRequestAsync(
            Arg.Any<CreatePrOptions>(), Arg.Any<CancellationToken>());
    }

    // ── 5. StaleExperiment_CountedAndContinues ───────────────────────────────

    [Fact]
    public async Task StaleExperiment_CountedAndContinues()
    {
        // Arrange: all experiments return stale comparison. After
        // StaleExperimentsBeforeStop stales, exits with "stale_limit".
        HoneConfig config = new(
            Loop: new LoopConfig(SkipClassification: true),
            Tolerances: new TolerancesConfig(
                StaleExperimentsBeforeStop: 3,
                MaxConsecutiveFailures: 999));

        TestHarness h = CreateHarness(
            configurePipeline: pipeline =>
            {
                _ = pipeline.RunAnalysisAsync(
                        Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new AnalysisResult(
                        Success: true, Opportunities: MakeOpportunities(count: 5))));
                _ = pipeline.RunLoadTestAsync(
                        Arg.Any<LoadTestInput>(), Arg.Any<CancellationToken>())
                    .Returns(callInfo => Task.FromResult(new LoadTestResult(
                        Success: true,
                        Metrics: MakeImprovedMetrics(
                            experiment: callInfo.Arg<LoadTestInput>().Experiment),
                        SummaryPath: null, Output: null)));
                _ = pipeline.CompareMetrics(
                        Arg.Any<MetricSet>(), Arg.Any<MetricSet>(), Arg.Any<MetricSet?>(),
                        Arg.Any<int>(), Arg.Any<HoneConfig>())
                    .Returns(StaleComparison());
            },
            config: config);

        // Act
        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 10));

        // Assert
        _ = result.ExitReason.Should().Be("stale_limit");
        _ = result.ExperimentsRun.Should().Be(3);
        _ = result.SuccessCount.Should().Be(0);
        _ = result.FailedExperiments.Should().HaveCount(3);
    }

    // ── 6. StackedDiffs_BranchChain ──────────────────────────────────────────

    [Fact]
    public async Task StackedDiffs_BranchChain()
    {
        // Arrange: 3 experiments all succeed → BranchChain has 3 entries,
        // PrChain has 3 entries, each PR base is the previous experiment's branch.
        TestHarness h = CreateHarness(configurePipeline: pipeline =>
        {
            _ = pipeline.RunAnalysisAsync(
                    Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AnalysisResult(
                    Success: true, Opportunities: MakeOpportunities(count: 5))));
            _ = pipeline.RunLoadTestAsync(
                    Arg.Any<LoadTestInput>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult(new LoadTestResult(
                    Success: true,
                    Metrics: MakeImprovedMetrics(
                        experiment: callInfo.Arg<LoadTestInput>().Experiment),
                    SummaryPath: null, Output: null)));
            _ = pipeline.CompareMetrics(
                    Arg.Any<MetricSet>(), Arg.Any<MetricSet>(), Arg.Any<MetricSet?>(),
                    Arg.Any<int>(), Arg.Any<HoneConfig>())
                .Returns(ImprovedComparison());
        });

        // Act
        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 3));

        // Assert — chain structure
        _ = result.ExitReason.Should().Be("max_experiments");
        _ = result.ExperimentsRun.Should().Be(3);
        _ = result.SuccessCount.Should().Be(3);

        _ = result.BranchChain.Should().HaveCount(3);
        _ = result.BranchChain[0].Should().Be("hone/experiment-1");
        _ = result.BranchChain[1].Should().Be("hone/experiment-2");
        _ = result.BranchChain[2].Should().Be("hone/experiment-3");

        _ = result.PrChain.Should().HaveCount(3);

        // Verify each experiment's push
        _ = await h.Pipeline.Received(3).PushBranchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _ = await h.Pipeline.Received(3).CreatePullRequestAsync(
            Arg.Any<CreatePrOptions>(), Arg.Any<CancellationToken>());
    }

    // ── 7. QueueRefill_AnalysisRerunsWhenEmpty ───────────────────────────────

    [Fact]
    public async Task QueueRefill_AnalysisRerunsWhenEmpty()
    {
        // Arrange: first analysis returns 1 item (used up by exp 1),
        // second analysis returns 1 more (used by exp 2).
        // Verify analysis ran twice.
        TestHarness h = CreateHarness(configurePipeline: pipeline =>
        {
            _ = pipeline.RunAnalysisAsync(
                    Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AnalysisResult(
                    Success: true, Opportunities: MakeOpportunities(count: 1))));
            _ = pipeline.RunLoadTestAsync(
                    Arg.Any<LoadTestInput>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult(new LoadTestResult(
                    Success: true,
                    Metrics: MakeImprovedMetrics(
                        experiment: callInfo.Arg<LoadTestInput>().Experiment),
                    SummaryPath: null, Output: null)));
            _ = pipeline.CompareMetrics(
                    Arg.Any<MetricSet>(), Arg.Any<MetricSet>(), Arg.Any<MetricSet?>(),
                    Arg.Any<int>(), Arg.Any<HoneConfig>())
                .Returns(ImprovedComparison());
        });

        // Act
        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 2));

        // Assert
        _ = result.ExperimentsRun.Should().Be(2);
        _ = result.SuccessCount.Should().Be(2);

        // Analysis should have been called twice (once per queue refill)
        _ = await h.Pipeline.Received(2).RunAnalysisAsync(
            Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>());
    }

    // ── 8. MaxExperiments_LoopStops ──────────────────────────────────────────

    [Fact]
    public async Task MaxExperiments_LoopStops()
    {
        // Arrange: analysis returns 5 opportunities, all succeed.
        // With maxExperiments=3, loop exits cleanly after exactly 3.
        TestHarness h = CreateHarness(configurePipeline: pipeline =>
        {
            _ = pipeline.RunAnalysisAsync(
                    Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AnalysisResult(
                    Success: true, Opportunities: MakeOpportunities(count: 5))));
            _ = pipeline.RunLoadTestAsync(
                    Arg.Any<LoadTestInput>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult(new LoadTestResult(
                    Success: true,
                    Metrics: MakeImprovedMetrics(
                        experiment: callInfo.Arg<LoadTestInput>().Experiment),
                    SummaryPath: null, Output: null)));
            _ = pipeline.CompareMetrics(
                    Arg.Any<MetricSet>(), Arg.Any<MetricSet>(), Arg.Any<MetricSet?>(),
                    Arg.Any<int>(), Arg.Any<HoneConfig>())
                .Returns(ImprovedComparison());
        });

        // Act
        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 3));

        // Assert
        _ = result.ExitReason.Should().Be("max_experiments");
        _ = result.ExperimentsRun.Should().Be(3);
        _ = result.SuccessCount.Should().Be(3);
        _ = result.FailedExperiments.Should().BeEmpty();

        // Only 1 analysis call needed (5 items > 3 experiments)
        _ = await h.Pipeline.Received(1).RunAnalysisAsync(
            Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>());
    }

    // ── 9. MaxConsecutiveFailures_LoopStops ──────────────────────────────────

    [Fact]
    public async Task MaxConsecutiveFailures_LoopStops()
    {
        // Arrange: all experiments fail at the build step.
        // After 3 consecutive failures the loop exits.
        HoneConfig config = new(
            Loop: new LoopConfig(SkipClassification: true),
            Tolerances: new TolerancesConfig(MaxConsecutiveFailures: 3));

        TestHarness h = CreateHarness(
            configurePipeline: pipeline =>
                _ = pipeline.RunAnalysisAsync(
                        Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new AnalysisResult(
                        Success: true, Opportunities: MakeOpportunities(count: 5)))),
            configureImplementer: implPipeline =>
                _ = implPipeline.BuildProjectAsync(
                        Arg.Any<BuildStepInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(
                        new BuildStepResult(Success: false, Output: "CS0001"))),
            config: config);

        // Act
        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 10));

        // Assert
        _ = result.ExitReason.Should().Be("max_consecutive_failures");
        _ = result.ExperimentsRun.Should().Be(3);
        _ = result.SuccessCount.Should().Be(0);
        _ = result.FailedExperiments.Should().HaveCount(3);
    }

    // ── 10. DryRun_SkipsSlowOps ─────────────────────────────────────────────

    [Fact]
    public async Task DryRun_SkipsSlowOps()
    {
        // Arrange: DryRun=true — load tests NOT called, synthetic metrics used,
        // analysis and compare still run.
        TestHarness h = CreateHarness(configurePipeline: pipeline =>
        {
            _ = pipeline.RunAnalysisAsync(
                    Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AnalysisResult(
                    Success: true, Opportunities: MakeOpportunities(count: 1))));
            _ = pipeline.CompareMetrics(
                    Arg.Any<MetricSet>(), Arg.Any<MetricSet>(), Arg.Any<MetricSet?>(),
                    Arg.Any<int>(), Arg.Any<HoneConfig>())
                .Returns(ImprovedComparison());
        });

        // Act
        LoopResult result = await h.Runner.RunAsync(
            h.MakeOptions(dryRun: true, maxExperiments: 1));

        // Assert
        _ = result.ExperimentsRun.Should().Be(1);
        _ = result.SuccessCount.Should().Be(1);

        // RunLoadTestAsync must NOT have been called (synthetic metrics used)
        _ = await h.Pipeline.DidNotReceive().RunLoadTestAsync(
            Arg.Any<LoadTestInput>(), Arg.Any<CancellationToken>());

        // Analysis was still called
        _ = await h.Pipeline.Received(1).RunAnalysisAsync(
            Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>());

        // CompareMetrics was still called (with synthetic metrics)
        _ = h.Pipeline.Received(1).CompareMetrics(
            Arg.Any<MetricSet>(), Arg.Any<MetricSet>(), Arg.Any<MetricSet?>(),
            Arg.Any<int>(), Arg.Any<HoneConfig>());
    }

    // ── 11. IterativeImplementer_RetryOnBuildFailure ─────────────────────────

    [Fact]
    public async Task IterativeImplementer_RetryOnBuildFailure()
    {
        // Arrange: first build attempt fails, second succeeds.
        // Iterative mode with MaxAttempts=3.
        HoneConfig config = new(
            Loop: new LoopConfig(SkipClassification: true),
            Implementer: new ImplementerConfig(MaxAttempts: 3));

        int buildCallCount = 0;
        TestHarness h = CreateHarness(
            configurePipeline: pipeline =>
            {
                _ = pipeline.RunAnalysisAsync(
                        Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new AnalysisResult(
                        Success: true, Opportunities: MakeOpportunities(count: 1))));
                _ = pipeline.RunLoadTestAsync(
                        Arg.Any<LoadTestInput>(), Arg.Any<CancellationToken>())
                    .Returns(callInfo => Task.FromResult(new LoadTestResult(
                        Success: true,
                        Metrics: MakeImprovedMetrics(
                            experiment: callInfo.Arg<LoadTestInput>().Experiment),
                        SummaryPath: null, Output: null)));
                _ = pipeline.CompareMetrics(
                        Arg.Any<MetricSet>(), Arg.Any<MetricSet>(), Arg.Any<MetricSet?>(),
                        Arg.Any<int>(), Arg.Any<HoneConfig>())
                    .Returns(ImprovedComparison());
            },
            configureImplementer: implPipeline =>
                _ = implPipeline.BuildProjectAsync(
                        Arg.Any<BuildStepInput>(), Arg.Any<CancellationToken>())
                    .Returns(_ =>
                    {
                        int call = Interlocked.Increment(ref buildCallCount);
                        return Task.FromResult(call == 1
                            ? new BuildStepResult(Success: false, Output: "Build error")
                            : new BuildStepResult(Success: true, Output: null));
                    }),
            config: config);

        // Act
        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 1));

        // Assert — experiment succeeded after retry
        _ = result.ExperimentsRun.Should().Be(1);
        _ = result.SuccessCount.Should().Be(1);
        _ = result.FailedExperiments.Should().BeEmpty();

        // Build was called twice (fail + succeed)
        _ = await h.ImplPipeline.Received(2).BuildProjectAsync(
            Arg.Any<BuildStepInput>(), Arg.Any<CancellationToken>());

        // RevertForRetry called once for the failed attempt
        await h.ImplPipeline.Received(1).RevertForRetryAsync(
            Arg.Any<RevertInput>(), Arg.Any<CancellationToken>());
    }

    // ── 12. IterativeImplementer_TestFileGuard ───────────────────────────────

    [Fact]
    public async Task IterativeImplementer_TestFileGuard()
    {
        // Arrange: implementation touches test files → guard rejects on every
        // attempt, implementer exhausts retry budget.
        // Tested directly via IterativeImplementerRunner because the loop
        // currently passes TestProjectPaths: null.
        string targetDir = CreateTargetDir("guard-target", b =>
            _ = b.AddFile("src/Service1.cs", "// code"));

        string resultsPath = "hone-results";
        Directory.CreateDirectory(Path.Combine(targetDir, resultsPath));

        IHoneEventSink eventSink = Substitute.For<IHoneEventSink>();
        IImplementerPipeline implPipeline = Substitute.For<IImplementerPipeline>();

        _ = implPipeline.InvokeFixAgentAsync(
                Arg.Any<FixStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SucceededFix()));
        _ = implPipeline.ApplySuggestionAsync(
                Arg.Any<ApplyStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(
                new ApplyStepResult(Success: true, CommitSha: "abc", Description: "fix")));
        _ = implPipeline.GetDiffLineCountAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(5));
        _ = implPipeline.BuildProjectAsync(
                Arg.Any<BuildStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new BuildStepResult(Success: true, Output: null)));
        _ = implPipeline.RunTestsAsync(
                Arg.Any<TestStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TestStepResult(Success: true, Output: null)));
        // Changed files include a test project path
        _ = implPipeline.GetChangedFilesAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(
                ["src/Service1.cs", "tests/UnitTests/SomeTest.cs"]));

        var implementer = new IterativeImplementerRunner(implPipeline, eventSink);

        var options = new ImplementerOptions(
            FilePath: "src/Service1.cs",
            Explanation: "Optimize hot path",
            RootCauseDocument: null,
            Experiment: 1,
            BaseBranch: "main",
            TargetDir: targetDir,
            TargetName: null,
            Config: new ImplementerConfig(MaxAttempts: 2, TestFileGuard: true),
            TestProjectPaths: ["tests/"],
            BranchPrefix: "hone/experiment",
            ResultsPath: resultsPath);

        // Act
        ImplementerRunResult result = await implementer.RunAsync(options);

        // Assert — guard rejected the implementation
        _ = result.Result.Success.Should().BeFalse();
        _ = result.Result.ExitReason.Should().Be("retry_budget_exhausted");
        _ = result.Result.AttemptCount.Should().Be(2);

        // Both attempts triggered guard → revert for retry on the first
        await implPipeline.Received(1).RevertForRetryAsync(
            Arg.Any<RevertInput>(), Arg.Any<CancellationToken>());
    }

    // ── 13. ObservabilityEvents_EmittedForAllPhases ──────────────────────────

    [Fact]
    public async Task ObservabilityEvents_EmittedForAllPhases()
    {
        // Arrange: happy path — verify all key observability events are emitted.
        TestHarness h = CreateHarness(configurePipeline: pipeline =>
        {
            _ = pipeline.RunAnalysisAsync(
                    Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AnalysisResult(
                    Success: true, Opportunities: MakeOpportunities(count: 1))));
            _ = pipeline.RunLoadTestAsync(
                    Arg.Any<LoadTestInput>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new LoadTestResult(
                    Success: true, Metrics: MakeImprovedMetrics(experiment: 1),
                    SummaryPath: null, Output: null)));
            _ = pipeline.CompareMetrics(
                    Arg.Any<MetricSet>(), Arg.Any<MetricSet>(), Arg.Any<MetricSet?>(),
                    Arg.Any<int>(), Arg.Any<HoneConfig>())
                .Returns(ImprovedComparison());
        });

        // Act
        _ = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 1));

        // Assert — key lifecycle events
        h.EventSink.Received().Emit(
            Arg.Is<PhaseStarted>(e => e.Phase == "loop"));
        h.EventSink.Received().Emit(
            Arg.Is<PhaseStarted>(e => e.Phase == "experiment" && e.Experiment == 1));
        h.EventSink.Received().Emit(
            Arg.Is<ExperimentOutcomeEvent>(
                e => e.Experiment == 1 && e.Outcome == "Improved"));
        h.EventSink.Received().Emit(
            Arg.Is<PhaseCompleted>(e => e.Phase == "loop" && e.Success));
    }

    // ── 14. StackedDiffs_MixedOutcomes_BranchAncestry ────────────────────────

    [Fact]
    public async Task StackedDiffs_MixedOutcomes_BranchAncestry()
    {
        // Arrange: 3 experiments — exp1=success, exp2=build_failure, exp3=success.
        // After exp2 fails, the loop reverts to exp1's branch.
        // Exp3 starts from exp1's branch (not exp2's).
        TestHarness h = CreateHarness(
            configurePipeline: pipeline =>
            {
                _ = pipeline.RunAnalysisAsync(
                        Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new AnalysisResult(
                        Success: true, Opportunities: MakeOpportunities(count: 5))));
                _ = pipeline.RunLoadTestAsync(
                        Arg.Any<LoadTestInput>(), Arg.Any<CancellationToken>())
                    .Returns(callInfo => Task.FromResult(new LoadTestResult(
                        Success: true,
                        Metrics: MakeImprovedMetrics(
                            experiment: callInfo.Arg<LoadTestInput>().Experiment),
                        SummaryPath: null, Output: null)));
                _ = pipeline.CompareMetrics(
                        Arg.Any<MetricSet>(), Arg.Any<MetricSet>(), Arg.Any<MetricSet?>(),
                        Arg.Any<int>(), Arg.Any<HoneConfig>())
                    .Returns(ImprovedComparison());
            },
            configureImplementer: implPipeline =>
                // Build fails only for experiment 2
                _ = implPipeline.BuildProjectAsync(
                        Arg.Any<BuildStepInput>(), Arg.Any<CancellationToken>())
                    .Returns(callInfo =>
                    {
                        int exp = callInfo.Arg<BuildStepInput>().Experiment;
                        return Task.FromResult(exp == 2
                            ? new BuildStepResult(Success: false, Output: "Build error")
                            : new BuildStepResult(Success: true, Output: null));
                    }));

        // Act
        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 3));

        // Assert — chain structure with gap at exp2
        _ = result.ExitReason.Should().Be("max_experiments");
        _ = result.ExperimentsRun.Should().Be(3);
        _ = result.SuccessCount.Should().Be(2);
        _ = result.FailedExperiments.Should().Equal([2]);

        // Branch chain: only successful experiments
        _ = result.BranchChain.Should().HaveCount(2);
        _ = result.BranchChain[0].Should().Be("hone/experiment-1");
        _ = result.BranchChain[1].Should().Be("hone/experiment-3");

        // PR chain: 2 PRs (one per successful experiment)
        _ = result.PrChain.Should().HaveCount(2);

        // Exp3's PR base branch is exp1's branch (skipping failed exp2)
        _ = await h.Pipeline.Received(1).CreatePullRequestAsync(
            Arg.Is<CreatePrOptions>(o =>
                o.HeadBranch == "hone/experiment-3"
                && o.BaseBranch == "hone/experiment-1"),
            Arg.Any<CancellationToken>());
    }
}
