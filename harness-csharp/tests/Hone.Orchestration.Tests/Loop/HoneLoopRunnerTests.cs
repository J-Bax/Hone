using FluentAssertions;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Orchestration.Failure;
using Hone.Orchestration.Implementer;
using Hone.Orchestration.Loop;
using Hone.Orchestration.Queue;
using Hone.TestInfrastructure;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Orchestration.Tests.Loop;

public sealed class HoneLoopRunnerTests(ITestOutputHelper output)
    : HoneTestBase(output)
{
    // ── Shared test helpers ─────────────────────────────────────────────────

    private static readonly MetricSet BaselineMetrics = new(
        Timestamp: "2024-01-01T00:00:00Z",
        Experiment: 0,
        Run: 1,
        HttpReqDuration: new HttpReqDurationMetrics(
            Avg: 100, P50: 90, P90: 140, P95: 150, P99: 180, Max: 250),
        HttpReqs: new HttpReqCountMetrics(Count: 10000, Rate: 500),
        HttpReqFailed: new HttpReqFailedMetrics(Count: 0, Rate: 0),
        SummaryPath: null);

    private static readonly MachineInfo TestMachine = new(
        CpuName: "Test CPU",
        CpuCores: 8,
        TotalRamGB: 32,
        OsVersion: "Test OS",
        DotnetVersion: "10.0.0");

    private static MetricSet MakeImprovedMetrics(int experiment) => new(
        Timestamp: DateTimeOffset.UtcNow.ToString("o"),
        Experiment: experiment,
        Run: 1,
        HttpReqDuration: new HttpReqDurationMetrics(
            Avg: 90, P50: 80, P90: 125, P95: 130, P99: 160, Max: 220),
        HttpReqs: new HttpReqCountMetrics(Count: 11000, Rate: 550),
        HttpReqFailed: new HttpReqFailedMetrics(Count: 0, Rate: 0),
        SummaryPath: null);

    private static ComparisonResult ImprovedComparison() => new(
        Accepted: true,
        Outcome: ExperimentOutcome.Improved,
        ImprovementPct: 0.13,
        RegressionPct: 0,
        Details: []);

    private static ComparisonResult StaleComparison() => new(
        Accepted: false,
        Outcome: ExperimentOutcome.Stale,
        ImprovementPct: 0.001,
        RegressionPct: 0,
        Details: []);

    private static List<Opportunity> MakeOpportunities(int count = 2)
    {
        var list = new List<Opportunity>(count);
        for (int i = 1; i <= count; i++)
        {
            list.Add(new Opportunity(
                FilePath: $"src/Service{i}.cs",
                Title: $"Optimise hot path {i}",
                Explanation: $"Method has O(n²) complexity #{i}",
                Scope: OpportunityScope.Narrow,
                RootCause: null,
                ImpactEstimate: null));
        }

        return list;
    }

    private static FixStepResult FailedFix() =>
        new(Success: false, CodeBlock: null,
            PromptPath: null, ResponsePath: null,
            AttemptPromptPath: null, AttemptResponsePath: null);

    private static FixStepResult SucceededFix() =>
        new(Success: true, CodeBlock: "// fixed",
            PromptPath: null, ResponsePath: null,
            AttemptPromptPath: null, AttemptResponsePath: null);

    private static HoneConfig CreateIterativeFailureConfig(
        int maxAttempts = 2,
        double maxDiffGrowthFactor = 3.0) =>
        new(
            Loop: new LoopConfig(SkipClassification: true),
            Implementer: new ImplementerConfig(
                MaxAttempts: maxAttempts,
                MaxDiffGrowthFactor: maxDiffGrowthFactor,
                TestFileGuard: false));

    /// <summary>
    /// Builds a fully wired HoneLoopRunner with mocked pipeline and real
    /// queue manager, implementer runner, and failure handler.
    /// </summary>
    private TestHarness CreateHarness(
        Action<ILoopPipeline>? configurePipeline = null,
        Action<IImplementerPipeline>? configureImplementer = null,
        HoneConfig? config = null)
    {
        string targetDir = CreateTargetDir("target", b =>
        {
            _ = b.AddFile("src/Service1.cs", "// original code 1");
            _ = b.AddFile("src/Service2.cs", "// original code 2");
        });

        string metadataDir = Path.Combine(targetDir, "hone-results", "metadata");
        Directory.CreateDirectory(metadataDir);

        IHoneEventSink eventSink = Substitute.For<IHoneEventSink>();
        IVersionControl versionControl = Substitute.For<IVersionControl>();

        // Queue manager (real, filesystem-backed)
        var queueManager = new OptimizationQueueManager(metadataDir, eventSink);

        // Implementer pipeline mock
        IImplementerPipeline implPipeline = Substitute.For<IImplementerPipeline>();
        _ = implPipeline.InvokeImplementerAgentAsync(Arg.Any<FixStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SucceededFix()));
        _ = implPipeline.ApplySuggestionAsync(Arg.Any<ApplyStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ApplyStepResult(Success: true, CommitSha: "abc123", Description: "fix")));
        _ = implPipeline.GetDiffLineCountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(5));
        _ = implPipeline.BuildProjectAsync(Arg.Any<BuildStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new BuildStepResult(Success: true, Output: null)));
        _ = implPipeline.RunTestsAsync(Arg.Any<TestStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TestStepResult(Success: true, Output: null)));
        configureImplementer?.Invoke(implPipeline);

        var implementer = new IterativeImplementerRunner(implPipeline, eventSink);

        // Failure handler (real, with mocked VCS)
        _ = versionControl.RevertLastCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var failureHandler = new ExperimentFailureHandler(versionControl, queueManager, eventSink);

        // Loop pipeline mock — sensible defaults
        ILoopPipeline pipeline = Substitute.For<ILoopPipeline>();
        ConfigureDefaultPipeline(pipeline);
        configurePipeline?.Invoke(pipeline);

        config ??= new HoneConfig(
            Loop: new LoopConfig(SkipClassification: true));

        var runner = new HoneLoopRunner(
            pipeline, queueManager, implementer, failureHandler, eventSink);

        return new TestHarness(
            Runner: runner,
            Pipeline: pipeline,
            ImplPipeline: implPipeline,
            EventSink: eventSink,
            VersionControl: versionControl,
            QueueManager: queueManager,
            TargetDir: targetDir,
            Config: config);
    }

    private static void ConfigureDefaultPipeline(ILoopPipeline pipeline)
    {
        _ = pipeline.LoadOrCreateBaselineAsync(
                Arg.Any<string>(), Arg.Any<HoneConfig>(), Arg.Any<Uri?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BaselineMetrics));
        _ = pipeline.GetMachineInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TestMachine));
        _ = pipeline.LoadRunMetadataAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RunMetadata?>(null));
        _ = pipeline.SaveRunMetadataAsync(
                Arg.Any<string>(), Arg.Any<RunMetadata>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _ = pipeline.PushBranchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PushResult(Success: true, Output: null)));
        _ = pipeline.CreatePullRequestAsync(
                Arg.Any<CreatePrOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                CreatePrOptions opts = callInfo.Arg<CreatePrOptions>();
                string lastSegment = opts.HeadBranch.Split('-')[^1];
                int prNum = int.Parse(lastSegment, System.Globalization.CultureInfo.InvariantCulture) + 100;
                return Task.FromResult(new PullRequestResult(
                    Success: true, PrNumber: prNum, PrUrl: new Uri($"https://github.com/test/pr/{prNum}")));
            });
        _ = pipeline.CommitArtifactsAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _ = pipeline.CheckoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _ = pipeline.ClassifyAsync(
                Arg.Any<ClassificationInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ClassificationResult(
                Success: true, Scope: OpportunityScope.Narrow)));
        _ = pipeline.StopTargetAsync(
                Arg.Any<string>(), Arg.Any<HoneConfig>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HookResult(
                Success: true, Message: "Stopped", Duration: TimeSpan.Zero, Artifacts: [], BaseUrl: null)));
        _ = pipeline.StartTargetAsync(
                Arg.Any<string>(), Arg.Any<HoneConfig>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HookResult(
                Success: true, Message: "Started", Duration: TimeSpan.FromSeconds(1),
                Artifacts: [], BaseUrl: new Uri("http://localhost:5050"))));
        _ = pipeline.PrepareAsync(
                Arg.Any<string>(), Arg.Any<HoneConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HookResult(
                Success: true, Message: "Prepared", Duration: TimeSpan.FromSeconds(2),
                Artifacts: [], BaseUrl: null)));
        _ = pipeline.WarmupAsync(
                Arg.Any<string>(), Arg.Any<HoneConfig>(), Arg.Any<Uri?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HookResult(
                Success: true, Message: "Warmed up", Duration: TimeSpan.Zero,
                Artifacts: [], BaseUrl: null)));
        _ = pipeline.CooldownAsync(
                Arg.Any<string>(), Arg.Any<HoneConfig>(), Arg.Any<Uri?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HookResult(
                Success: true, Message: "Cooled down", Duration: TimeSpan.Zero,
                Artifacts: [], BaseUrl: null)));
        _ = pipeline.CleanupAsync(
                Arg.Any<string>(), Arg.Any<HoneConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HookResult(
                Success: true, Message: "Cleaned up", Duration: TimeSpan.Zero,
                Artifacts: [], BaseUrl: null)));
    }

    private sealed record TestHarness(
        HoneLoopRunner Runner,
        ILoopPipeline Pipeline,
        IImplementerPipeline ImplPipeline,
        IHoneEventSink EventSink,
        IVersionControl VersionControl,
        OptimizationQueueManager QueueManager,
        string TargetDir,
        HoneConfig Config)
    {
        internal LoopOptions MakeOptions(
            bool dryRun = false,
            int? maxExperiments = null) =>
            new(
                TargetDir: TargetDir,
                Config: Config,
                TargetName: "test-target",
                DefaultBranch: "main",
                ResultsPath: "hone-results",
                DryRun: dryRun,
                MaxExperimentsOverride: maxExperiments);
    }

    [Fact]
    public async Task RunAsync_CreatesBaselineAfterPrepareAndStart()
    {
        TestHarness h = CreateHarness();

        _ = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 0));

        Received.InOrder(() =>
        {
            _ = h.Pipeline.PrepareAsync(h.TargetDir, h.Config, Arg.Any<CancellationToken>());
            _ = h.Pipeline.StartTargetAsync(h.TargetDir, h.Config, 0, Arg.Any<CancellationToken>());
            _ = h.Pipeline.LoadOrCreateBaselineAsync(
                h.TargetDir,
                h.Config,
                Arg.Is<Uri?>(uri => uri == new Uri("http://localhost:5050")),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task RunAsync_PassesRuntimeBaseUrlIntoLoadTests()
    {
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

        _ = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 1));

        _ = await h.Pipeline.Received(1).RunLoadTestAsync(
            Arg.Is<LoadTestInput>(input =>
                input.Experiment == 1
                && input.BaseUrl == new Uri("http://localhost:5050")),
            Arg.Any<CancellationToken>());
    }

    // ── 1. HappyPath_SingleExperiment_Accepted ──────────────────────────────

    [Fact]
    public async Task HappyPath_SingleExperiment_Accepted()
    {
        // Arrange
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
        await h.Pipeline.Received(1).SaveRunMetadataAsync(
            Arg.Any<string>(), Arg.Any<RunMetadata>(), Arg.Any<CancellationToken>());
    }

    // ── 2. StackedDiffs_FailedExperiment_RevertedContinues ──────────────────

    [Fact]
    public async Task StackedDiffs_FailedExperiment_RevertedContinues()
    {
        // Arrange: 2 opportunities. Experiment 1 impl fails, experiment 2 succeeds.
        int fixCallCount = 0;
        TestHarness h = CreateHarness(
            configurePipeline: pipeline =>
            {
                _ = pipeline.RunAnalysisAsync(
                        Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new AnalysisResult(
                        Success: true, Opportunities: MakeOpportunities(count: 2))));
                _ = pipeline.RunLoadTestAsync(
                        Arg.Any<LoadTestInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new LoadTestResult(
                        Success: true, Metrics: MakeImprovedMetrics(experiment: 2),
                        SummaryPath: null, Output: null)));
                _ = pipeline.CompareMetrics(
                        Arg.Any<MetricSet>(), Arg.Any<MetricSet>(), Arg.Any<MetricSet?>(),
                        Arg.Any<int>(), Arg.Any<HoneConfig>())
                    .Returns(ImprovedComparison());
            },
            configureImplementer: implPipeline =>
                _ = implPipeline.InvokeImplementerAgentAsync(
                        Arg.Any<FixStepInput>(), Arg.Any<CancellationToken>())
                    .Returns(_ =>
                    {
                        int n = Interlocked.Increment(ref fixCallCount);
                        return Task.FromResult(n == 1
                            ? FailedFix()
                            : SucceededFix());
                    }));

        // Act
        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 2));

        // Assert
        _ = result.ExperimentsRun.Should().Be(2);
        _ = result.SuccessCount.Should().Be(1);
        _ = result.FailedExperiments.Should().ContainSingle().Which.Should().Be(1);
        _ = result.BranchChain.Should().ContainSingle()
            .Which.Should().Be("hone/experiment-2");

        // Failure handler should have reverted once
        await h.VersionControl.Received(1).RevertLastCommitAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Checkout back to base branch after failure
        await h.Pipeline.Received(1).CheckoutAsync(
            Arg.Any<string>(), Arg.Is("main"), Arg.Any<CancellationToken>());
    }

    // ── 3. QueueDriven_ReanalyzesWhenEmpty ──────────────────────────────────

    [Fact]
    public async Task QueueDriven_ReanalyzesWhenEmpty()
    {
        // Arrange: each analysis returns 1 item, consumed per experiment.
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
                    Metrics: MakeImprovedMetrics(experiment: callInfo.Arg<LoadTestInput>().Experiment),
                    SummaryPath: null, Output: null)));
            _ = pipeline.CompareMetrics(
                    Arg.Any<MetricSet>(), Arg.Any<MetricSet>(), Arg.Any<MetricSet?>(),
                    Arg.Any<int>(), Arg.Any<HoneConfig>())
                .Returns(ImprovedComparison());
        });

        // Act
        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 2));

        // Assert
        _ = result.SuccessCount.Should().Be(2);

        _ = await h.Pipeline.Received(2).RunAnalysisAsync(
            Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>());
    }

    // ── 4. MaxExperiments_StopsLoop ─────────────────────────────────────────

    [Fact]
    public async Task MaxExperiments_StopsLoop()
    {
        // Arrange: analysis returns many items but maxExp = 3
        TestHarness h = CreateHarness(configurePipeline: pipeline =>
        {
            _ = pipeline.RunAnalysisAsync(
                    Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AnalysisResult(
                    Success: true, Opportunities: MakeOpportunities(count: 10))));
            _ = pipeline.RunLoadTestAsync(
                    Arg.Any<LoadTestInput>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult(new LoadTestResult(
                    Success: true,
                    Metrics: MakeImprovedMetrics(experiment: callInfo.Arg<LoadTestInput>().Experiment),
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
    }

    // ── 5. MaxConsecutiveFailures_StopsLoop ──────────────────────────────────

    [Fact]
    public async Task MaxConsecutiveFailures_StopsLoop()
    {
        // Arrange: all experiments fail (fix agent fails every time)
        HoneConfig config = new(
            Loop: new LoopConfig(SkipClassification: true),
            Tolerances: new TolerancesConfig(MaxConsecutiveFailures: 2));

        TestHarness h = CreateHarness(
            configurePipeline: pipeline =>
                _ = pipeline.RunAnalysisAsync(
                        Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new AnalysisResult(
                        Success: true, Opportunities: MakeOpportunities(count: 5)))),
            configureImplementer: implPipeline =>
                _ = implPipeline.InvokeImplementerAgentAsync(
                        Arg.Any<FixStepInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(FailedFix())),
            config: config);

        // Act
        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 10));

        // Assert
        _ = result.ExitReason.Should().Be("max_consecutive_failures");
        _ = result.ExperimentsRun.Should().Be(2);
        _ = result.SuccessCount.Should().Be(0);
        _ = result.FailedExperiments.Should().HaveCount(2);
    }

    [Fact]
    public async Task IterativeBuildExhaustion_MapsToBuildFailed()
    {
        RunMetadata? savedMetadata = null;
        TestHarness h = CreateHarness(
            configurePipeline: pipeline =>
            {
                _ = pipeline.RunAnalysisAsync(
                        Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new AnalysisResult(
                        Success: true, Opportunities: MakeOpportunities(count: 1))));
                _ = pipeline.SaveRunMetadataAsync(
                        Arg.Any<string>(), Arg.Any<RunMetadata>(), Arg.Any<CancellationToken>())
                    .Returns(callInfo =>
                    {
                        savedMetadata = callInfo.Arg<RunMetadata>();
                        return Task.CompletedTask;
                    });
            },
            configureImplementer: implPipeline =>
                _ = implPipeline.BuildProjectAsync(
                        Arg.Any<BuildStepInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new BuildStepResult(
                        Success: false, Output: "build failed"))),
            config: CreateIterativeFailureConfig());

        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 1));

        _ = result.SuccessCount.Should().Be(0);
        _ = result.FailedExperiments.Should().ContainSingle().Which.Should().Be(1);
        _ = savedMetadata.Should().NotBeNull();
        _ = savedMetadata!.Experiments.Should().ContainSingle();
        _ = savedMetadata.Experiments[0].Outcome.Should().Be(ExperimentOutcome.BuildFailed);
    }

    [Fact]
    public async Task IterativeTestExhaustion_MapsToTestFailed()
    {
        RunMetadata? savedMetadata = null;
        TestHarness h = CreateHarness(
            configurePipeline: pipeline =>
            {
                _ = pipeline.RunAnalysisAsync(
                        Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new AnalysisResult(
                        Success: true, Opportunities: MakeOpportunities(count: 1))));
                _ = pipeline.SaveRunMetadataAsync(
                        Arg.Any<string>(), Arg.Any<RunMetadata>(), Arg.Any<CancellationToken>())
                    .Returns(callInfo =>
                    {
                        savedMetadata = callInfo.Arg<RunMetadata>();
                        return Task.CompletedTask;
                    });
            },
            configureImplementer: implPipeline =>
                _ = implPipeline.RunTestsAsync(
                        Arg.Any<TestStepInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new TestStepResult(
                        Success: false, Output: "tests failed"))),
            config: CreateIterativeFailureConfig());

        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 1));

        _ = result.SuccessCount.Should().Be(0);
        _ = result.FailedExperiments.Should().ContainSingle().Which.Should().Be(1);
        _ = savedMetadata.Should().NotBeNull();
        _ = savedMetadata!.Experiments.Should().ContainSingle();
        _ = savedMetadata.Experiments[0].Outcome.Should().Be(ExperimentOutcome.TestFailed);
    }

    [Fact]
    public async Task IterativeNonBuildOrTestExhaustion_FallsBackToImplementationFailed()
    {
        RunMetadata? savedMetadata = null;
        int diffCallCount = 0;
        int buildCallCount = 0;
        TestHarness h = CreateHarness(
            configurePipeline: pipeline =>
            {
                _ = pipeline.RunAnalysisAsync(
                        Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new AnalysisResult(
                        Success: true, Opportunities: MakeOpportunities(count: 1))));
                _ = pipeline.SaveRunMetadataAsync(
                        Arg.Any<string>(), Arg.Any<RunMetadata>(), Arg.Any<CancellationToken>())
                    .Returns(callInfo =>
                    {
                        savedMetadata = callInfo.Arg<RunMetadata>();
                        return Task.CompletedTask;
                    });
            },
            configureImplementer: implPipeline =>
            {
                _ = implPipeline.GetDiffLineCountAsync(
                        Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(_ => Task.FromResult(Interlocked.Increment(ref diffCallCount) == 1 ? 1 : 10));
                _ = implPipeline.BuildProjectAsync(
                        Arg.Any<BuildStepInput>(), Arg.Any<CancellationToken>())
                    .Returns(_ => Task.FromResult(Interlocked.Increment(ref buildCallCount) == 1
                        ? new BuildStepResult(Success: false, Output: "build failed")
                        : new BuildStepResult(Success: true, Output: null)));
            },
            config: CreateIterativeFailureConfig(maxDiffGrowthFactor: 2.0));

        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 1));

        _ = result.SuccessCount.Should().Be(0);
        _ = result.FailedExperiments.Should().ContainSingle().Which.Should().Be(1);
        _ = savedMetadata.Should().NotBeNull();
        _ = savedMetadata!.Experiments.Should().ContainSingle();
        _ = savedMetadata.Experiments[0].Outcome.Should().Be(ExperimentOutcome.ImplementationFailed);
    }

    // ── 6. DryRun_SkipsSlowOperations ───────────────────────────────────────

    [Fact]
    public async Task DryRun_SkipsSlowOperations()
    {
        // Arrange
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
        _ = result.SuccessCount.Should().Be(1);
        _ = result.ExitReason.Should().Be("max_experiments");

        // RunLoadTestAsync must NOT have been called in DryRun
        _ = await h.Pipeline.DidNotReceive().RunLoadTestAsync(
            Arg.Any<LoadTestInput>(), Arg.Any<CancellationToken>());

        // CompareMetrics should have been called with synthetic P95 (~142.5 = 150 * 0.95)
        _ = h.Pipeline.Received(1).CompareMetrics(
            Arg.Is<MetricSet>(m => m.HttpReqDuration.P95 < BaselineMetrics.HttpReqDuration.P95),
            Arg.Any<MetricSet>(),
            Arg.Any<MetricSet?>(),
            Arg.Any<int>(),
            Arg.Any<HoneConfig>());
    }

    // ── 7. ExperimentMetadata_Consistent ────────────────────────────────────

    [Fact]
    public async Task ExperimentMetadata_Consistent()
    {
        // Arrange: run 2 experiments. Capture SaveRunMetadataAsync calls.
        RunMetadata? savedMetadata = null;
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
                .Returns(ImprovedComparison());
            _ = pipeline.SaveRunMetadataAsync(
                    Arg.Any<string>(), Arg.Any<RunMetadata>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    savedMetadata = callInfo.Arg<RunMetadata>();
                    return Task.CompletedTask;
                });
        });

        // Act
        _ = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 2));

        // Assert — savedMetadata should have 2 experiments
        _ = savedMetadata.Should().NotBeNull();
        _ = savedMetadata!.TargetName.Should().Be("test-target");
        _ = savedMetadata.MachineInfo.Should().NotBeNull();
        _ = savedMetadata.Experiments.Should().HaveCount(2);

        ExperimentMetadata exp1 = savedMetadata.Experiments[0];
        _ = exp1.Experiment.Should().Be(1);
        _ = exp1.Outcome.Should().Be(ExperimentOutcome.Improved);
        _ = exp1.BranchName.Should().Be("hone/experiment-1");
        _ = exp1.BaseBranch.Should().Be("main");
        _ = exp1.P95.Should().BeApproximately(130, precision: 1);
        _ = exp1.PrNumber.Should().NotBeNull();
        _ = exp1.ConsecutiveFailures.Should().Be(0);

        ExperimentMetadata exp2 = savedMetadata.Experiments[1];
        _ = exp2.Experiment.Should().Be(2);
        _ = exp2.Outcome.Should().Be(ExperimentOutcome.Improved);
        _ = exp2.BranchName.Should().Be("hone/experiment-2");
        // In stacked-diffs mode, base branch = previous experiment branch
        _ = exp2.BaseBranch.Should().Be("hone/experiment-1");
        _ = exp2.PrNumber.Should().NotBeNull();
    }

    // ── 8. SyntheticMetrics_FivePercentImprovement ──────────────────────────

    [Fact]
    public void SyntheticMetrics_FivePercentImprovement()
    {
        // Act
        MetricSet synthetic = HoneLoopRunner.GenerateSyntheticMetrics(
            reference: BaselineMetrics, experiment: 1);

        // Assert
        double expectedP95 = BaselineMetrics.HttpReqDuration.P95 * 0.95;
        _ = synthetic.HttpReqDuration.P95.Should().BeApproximately(expectedP95, precision: 0.01);
        _ = synthetic.HttpReqDuration.Avg.Should().BeApproximately(
            BaselineMetrics.HttpReqDuration.Avg * 0.95, precision: 0.01);
        _ = synthetic.HttpReqs.Rate.Should().BeGreaterThan(BaselineMetrics.HttpReqs.Rate);
        _ = synthetic.Experiment.Should().Be(1);
    }

    // ── 9. NoOpportunities_ExitsEarly ───────────────────────────────────────

    [Fact]
    public async Task NoOpportunities_ExitsEarly()
    {
        // Arrange: analysis returns no opportunities
        TestHarness h = CreateHarness(configurePipeline: pipeline =>
            _ = pipeline.RunAnalysisAsync(
                    Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AnalysisResult(
                    Success: true, Opportunities: []))));

        // Act
        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 5));

        // Assert
        _ = result.ExitReason.Should().Be("no_opportunities");
        _ = result.ExperimentsRun.Should().Be(0);
        _ = result.SuccessCount.Should().Be(0);
    }

    // ── 10. StaleLimit_StopsLoop ────────────────────────────────────────────

    [Fact]
    public async Task StaleLimit_StopsLoop()
    {
        // Arrange: all experiments are stale, stale limit = 2
        HoneConfig config = new(
            Loop: new LoopConfig(SkipClassification: true),
            Tolerances: new TolerancesConfig(
                StaleExperimentsBeforeStop: 2,
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
        _ = result.ExperimentsRun.Should().Be(2);
        _ = result.SuccessCount.Should().Be(0);
        _ = result.FailedExperiments.Should().HaveCount(2);
    }

    // ── 11. Legacy_Regression_BreaksLoop ─────────────────────────────────────

    [Fact]
    public async Task Legacy_Regression_BreaksLoop()
    {
        // Arrange: legacy mode (StackedDiffs=false), experiment regresses
        HoneConfig config = new(
            Loop: new LoopConfig(SkipClassification: true, StackedDiffs: false));

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
                    .Returns(new ComparisonResult(
                        Accepted: false,
                        Outcome: ExperimentOutcome.Regressed,
                        ImprovementPct: -0.15,
                        RegressionPct: 0.15,
                        Details: []));
            },
            config: config);

        // Act
        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 10));

        // Assert — should break after first experiment with regression exit reason
        _ = result.ExitReason.Should().Be("regressed");
        _ = result.ExperimentsRun.Should().Be(1);
        _ = result.SuccessCount.Should().Be(0);
        _ = result.FailedExperiments.Should().ContainSingle().Which.Should().Be(1);
    }

    // ── 12. Architecture_SkipInnerLoop ──────────────────────────────────────

    [Fact]
    public async Task Architecture_SkipInnerLoop()
    {
        // Arrange: 2 opportunities — first is architecture (skipped), second is narrow.
        // Classification enabled so the inner loop can skip architecture items.
        HoneConfig config = new(
            Loop: new LoopConfig(SkipClassification: false));

        TestHarness h = CreateHarness(
            configurePipeline: pipeline =>
            {
                _ = pipeline.RunAnalysisAsync(
                        Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new AnalysisResult(
                        Success: true, Opportunities: MakeOpportunities(count: 2))));
                _ = pipeline.ClassifyAsync(
                        Arg.Any<ClassificationInput>(), Arg.Any<CancellationToken>())
                    .Returns(callInfo =>
                    {
                        string filePath = callInfo.Arg<ClassificationInput>().FilePath;
                        OpportunityScope scope = filePath.Contains("Service1", StringComparison.Ordinal)
                            ? OpportunityScope.Architecture
                            : OpportunityScope.Narrow;
                        return Task.FromResult(new ClassificationResult(
                            Success: true, Scope: scope));
                    });
                _ = pipeline.RunLoadTestAsync(
                        Arg.Any<LoadTestInput>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new LoadTestResult(
                        Success: true, Metrics: MakeImprovedMetrics(experiment: 1),
                        SummaryPath: null, Output: null)));
                _ = pipeline.CompareMetrics(
                        Arg.Any<MetricSet>(), Arg.Any<MetricSet>(), Arg.Any<MetricSet?>(),
                        Arg.Any<int>(), Arg.Any<HoneConfig>())
                    .Returns(ImprovedComparison());
            },
            config: config);

        // Act — only 1 experiment slot; architecture skip must not consume it
        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 1));

        // Assert
        _ = result.ExperimentsRun.Should().Be(1);
        _ = result.SuccessCount.Should().Be(1);

        // Classification called twice: once for architecture item, once for narrow item
        _ = await h.Pipeline.Received(2).ClassifyAsync(
            Arg.Any<ClassificationInput>(), Arg.Any<CancellationToken>());
    }

    // ── 13. VerificationFailure_RevertsContinues ────────────────────────────

    [Fact]
    public async Task VerificationFailure_RevertsContinues()
    {
        // Arrange: stacked-diffs mode. First experiment load test fails,
        // second experiment succeeds.
        int loadTestCallCount = 0;
        TestHarness h = CreateHarness(configurePipeline: pipeline =>
        {
            _ = pipeline.RunAnalysisAsync(
                    Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AnalysisResult(
                    Success: true, Opportunities: MakeOpportunities(count: 2))));
            _ = pipeline.RunLoadTestAsync(
                    Arg.Any<LoadTestInput>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    int n = Interlocked.Increment(ref loadTestCallCount);
                    return n == 1
                        ? Task.FromResult(new LoadTestResult(
                            Success: false, Metrics: null,
                            SummaryPath: null, Output: "Load test crashed"))
                        : Task.FromResult(new LoadTestResult(
                            Success: true,
                            Metrics: MakeImprovedMetrics(experiment: 2),
                            SummaryPath: null, Output: null));
                });
            _ = pipeline.CompareMetrics(
                    Arg.Any<MetricSet>(), Arg.Any<MetricSet>(), Arg.Any<MetricSet?>(),
                    Arg.Any<int>(), Arg.Any<HoneConfig>())
                .Returns(ImprovedComparison());
        });

        // Act
        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 2));

        // Assert — first experiment reverted, second accepted
        _ = result.ExperimentsRun.Should().Be(2);
        _ = result.SuccessCount.Should().Be(1);
        _ = result.FailedExperiments.Should().ContainSingle().Which.Should().Be(1);

        // Failure handler reverted the failed experiment
        await h.VersionControl.Received(1).RevertLastCommitAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
