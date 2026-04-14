using System.Text.Json;
using FluentAssertions;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Measurement.Comparison;
using Hone.Orchestration.Implementer;
using Hone.Orchestration.Loop;
using Hone.TestInfrastructure.HarnessTesting;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Integration.Tests;

public sealed class HarnessScenarioFrameworkTests(ITestOutputHelper output)
    : IntegrationTestBase(output)
{
    private static Dictionary<string, HarnessLoopScenario> ScenarioCatalog { get; } =
        new Dictionary<string, HarnessLoopScenario>(StringComparer.Ordinal)
        {
            ["happy-path-improvement"] = BuildHappyPathScenario(),
            ["build-failure-rejection"] = BuildBuildFailureScenario(),
            ["test-failure-rejection"] = BuildTestFailureScenario(),
            ["performance-regression-rejection"] = BuildPerformanceRegressionScenario(),
            ["stale-outcome-limit"] = BuildStaleScenario(),
            ["queue-refill-and-consumption"] = BuildQueueRefillScenario(),
            ["stacked-diffs-success-failure-success"] = BuildStackedDiffsScenario(),
            ["resume-from-partial-state"] = BuildResumeScenario(),
            ["start-hook-failure"] = BuildStartFailureScenario(),
            ["load-test-failure"] = BuildLoadTestFailureScenario(),
        };

    public static TheoryData<string> ScenarioMatrix =>
    [
        "happy-path-improvement",
        "build-failure-rejection",
        "test-failure-rejection",
        "performance-regression-rejection",
        "stale-outcome-limit",
        "queue-refill-and-consumption",
        "stacked-diffs-success-failure-success",
        "resume-from-partial-state",
        "start-hook-failure",
        "load-test-failure",
    ];

    [Theory]
    [MemberData(nameof(ScenarioMatrix))]
    public async Task ScenarioMatrix_EnforcesHarnessContracts(string scenarioName)
    {
        HarnessLoopScenario scenario = ScenarioCatalog[scenarioName];
        HarnessScenarioRunResult run = await RunScenarioAsync(scenario);
        scenario.Assert(run);
    }

    private async Task<HarnessScenarioRunResult> RunScenarioAsync(HarnessLoopScenario scenario)
    {
        HarnessFixtureCatalog fixtures = CreateHarnessFixtureCatalog();
        RunMetadata? capturedMetadata = null;
        List<HoneEvent> emittedEvents = [];

        TestHarness harness = CreateHarness(
            configurePipeline: pipeline =>
            {
                scenario.ConfigurePipeline?.Invoke(fixtures, pipeline);
                _ = pipeline.SaveRunMetadataAsync(
                        Arg.Any<string>(), Arg.Any<RunMetadata>(), Arg.Any<CancellationToken>())
                    .Returns(callInfo =>
                    {
                        string path = callInfo.ArgAt<string>(0);
                        capturedMetadata = callInfo.ArgAt<RunMetadata>(1);
                        WriteJson(path, capturedMetadata);
                        return Task.CompletedTask;
                    });
            },
            configureImplementer: impl => scenario.ConfigureImplementer?.Invoke(fixtures, impl),
            config: scenario.Config,
            targetFixture: scenario.TargetFixture,
            configureTarget: targetDir => scenario.ConfigureTarget?.Invoke(fixtures, targetDir));

        harness.EventSink
            .When(sink => sink.Emit(Arg.Any<HoneEvent>()))
            .Do(call => emittedEvents.Add(call.Arg<HoneEvent>()));

        LoopResult loopResult = await harness.Runner.RunAsync(
            harness.MakeOptions(dryRun: scenario.DryRun, maxExperiments: scenario.MaxExperiments)).ConfigureAwait(false);

        string metadataPath = Path.Combine(harness.TargetDir, "hone-results", "run-metadata.json");
        RunMetadata runMetadata = capturedMetadata
            ?? JsonSerializer.Deserialize<RunMetadata>(
                await File.ReadAllTextAsync(metadataPath).ConfigureAwait(false),
                MetadataJsonOptions)
            ?? throw new InvalidOperationException($"Run metadata was not captured for scenario '{scenario.Name}'.");

        return new HarnessScenarioRunResult(
            scenario,
            harness,
            loopResult,
            runMetadata,
            metadataPath,
            Path.Combine(harness.TargetDir, "hone-results", "metadata", "experiment-queue.json"),
            emittedEvents);
    }

    private static HarnessLoopScenario BuildHappyPathScenario() =>
        new(
            name: "happy-path-improvement",
            targetFixture: "happy-path",
            maxExperiments: 1,
            config: DefaultScenarioConfig(),
            configureTarget: null,
            configurePipeline: (fixtures, pipeline) =>
            {
                ConfigureAnalysisSequence(fixtures, pipeline, "single-opportunity-a.json");
                ConfigureMetricFixtures(
                    fixtures,
                    pipeline,
                    (Experiment: 1, FixtureName: "improved-step-1.json"));
                ConfigureRealMetricComparer(pipeline);
            },
            configureImplementer: null,
            assertScenario: run =>
            {
                _ = run.LoopResult.ExitReason.Should().Be("max_experiments");
                _ = run.LoopResult.ExperimentsRun.Should().Be(1);
                _ = run.LoopResult.SuccessCount.Should().Be(1);
                _ = run.LoopResult.FailedExperiments.Should().BeEmpty();
                _ = run.LoopResult.BranchChain.Should().Equal(["hone/experiment-1"]);

                HarnessContractAssertions.AssertRunMetadataContracts(
                    run.RunMetadata,
                    expectedTargetName: "test-target",
                    expectedExperiments:
                    [
                        new ExpectedExperimentContract(
                            Experiment: 1,
                            Outcome: ExperimentOutcome.Improved,
                            BranchName: "hone/experiment-1",
                            BaseBranch: "main",
                            HasPullRequest: true,
                            HasMetrics: true),
                    ]);
                HarnessContractAssertions.AssertSuccessfulBranchLineage(run.RunMetadata);
                HarnessContractAssertions.AssertQueueContracts(
                    run.QueueJsonPath,
                    expectedGeneratedByExperiment: 1,
                    expectedItems:
                    [
                        new ExpectedQueueItemContract(
                            Id: "1",
                            FilePath: "src/Service1.cs",
                            Status: QueueItemStatus.Done,
                            TriedByExperiment: 1,
                            Outcome: "improved"),
                    ]);

                _ = File.Exists(Path.Combine(run.ResultsRoot, "experiment-1", "k6-summary.json")).Should().BeTrue();
                _ = File.Exists(run.MetadataPath).Should().BeTrue();
            });

    private static HarnessLoopScenario BuildBuildFailureScenario() =>
        new(
            name: "build-failure-rejection",
            targetFixture: "build-failure",
            maxExperiments: 2,
            config: SingleAttemptScenarioConfig(),
            configureTarget: null,
            configurePipeline: (fixtures, pipeline) =>
                ConfigureAnalysisSequence(fixtures, pipeline, "two-opportunities.json"),
            configureImplementer: (_, implementer) =>
                ConfigureBuildFailures(implementer, failingExperiments: [1, 2]),
            assertScenario: run =>
            {
                _ = run.LoopResult.ExperimentsRun.Should().Be(2);
                _ = run.LoopResult.SuccessCount.Should().Be(0);
                _ = run.LoopResult.FailedExperiments.Should().Equal([1, 2]);
                _ = run.PullRequestCalls.Should().HaveCount(2);

                HarnessContractAssertions.AssertRunMetadataContracts(
                    run.RunMetadata,
                    "test-target",
                    [
                        new ExpectedExperimentContract(
                            Experiment: 1,
                            Outcome: ExperimentOutcome.BuildFailed,
                            BranchName: "hone/experiment-1",
                            BaseBranch: "main",
                            HasPullRequest: true,
                            HasMetrics: false),
                        new ExpectedExperimentContract(
                            Experiment: 2,
                            Outcome: ExperimentOutcome.BuildFailed,
                            BranchName: "hone/experiment-2",
                            BaseBranch: "main",
                            HasPullRequest: true,
                            HasMetrics: false),
                    ]);
                HarnessContractAssertions.AssertQueueContracts(
                    run.QueueJsonPath,
                    1,
                    [
                        new ExpectedQueueItemContract(
                            Id: "1",
                            FilePath: "src/Service1.cs",
                            Status: QueueItemStatus.Done,
                            TriedByExperiment: 1,
                            Outcome: "build_failed"),
                        new ExpectedQueueItemContract(
                            Id: "2",
                            FilePath: "src/Service2.cs",
                            Status: QueueItemStatus.Done,
                            TriedByExperiment: 2,
                            Outcome: "build_failed"),
                    ]);

                _ = File.Exists(Path.Combine(run.ResultsRoot, "experiment-1", "iteration-log.json")).Should().BeTrue();
                _ = File.Exists(Path.Combine(run.ResultsRoot, "experiment-1", "iterations", "attempt-1", "build.log")).Should().BeTrue();
            });

    private static HarnessLoopScenario BuildTestFailureScenario() =>
        new(
            name: "test-failure-rejection",
            targetFixture: "test-failure",
            maxExperiments: 2,
            config: SingleAttemptScenarioConfig(),
            configureTarget: null,
            configurePipeline: (fixtures, pipeline) =>
                ConfigureAnalysisSequence(fixtures, pipeline, "two-opportunities.json"),
            configureImplementer: (_, implementer) =>
                ConfigureTestFailures(implementer, failingExperiments: [1, 2]),
            assertScenario: run =>
            {
                _ = run.LoopResult.ExperimentsRun.Should().Be(2);
                _ = run.LoopResult.SuccessCount.Should().Be(0);
                _ = run.LoopResult.FailedExperiments.Should().Equal([1, 2]);
                _ = run.PullRequestCalls.Should().HaveCount(2);

                HarnessContractAssertions.AssertRunMetadataContracts(
                    run.RunMetadata,
                    "test-target",
                    [
                        new ExpectedExperimentContract(
                            Experiment: 1,
                            Outcome: ExperimentOutcome.TestFailed,
                            BranchName: "hone/experiment-1",
                            BaseBranch: "main",
                            HasPullRequest: true,
                            HasMetrics: false),
                        new ExpectedExperimentContract(
                            Experiment: 2,
                            Outcome: ExperimentOutcome.TestFailed,
                            BranchName: "hone/experiment-2",
                            BaseBranch: "main",
                            HasPullRequest: true,
                            HasMetrics: false),
                    ]);
                HarnessContractAssertions.AssertQueueContracts(
                    run.QueueJsonPath,
                    1,
                    [
                        new ExpectedQueueItemContract(
                            Id: "1",
                            FilePath: "src/Service1.cs",
                            Status: QueueItemStatus.Done,
                            TriedByExperiment: 1,
                            Outcome: "test_failed"),
                        new ExpectedQueueItemContract(
                            Id: "2",
                            FilePath: "src/Service2.cs",
                            Status: QueueItemStatus.Done,
                            TriedByExperiment: 2,
                            Outcome: "test_failed"),
                    ]);

                _ = File.Exists(Path.Combine(run.ResultsRoot, "experiment-1", "iterations", "attempt-1", "e2e-tests.log")).Should().BeTrue();
                _ = File.Exists(Path.Combine(run.ResultsRoot, "experiment-1", "iterations", "attempt-1", "e2e-results.trx")).Should().BeTrue();
            });

    private static HarnessLoopScenario BuildPerformanceRegressionScenario() =>
        new(
            name: "performance-regression-rejection",
            targetFixture: "regression",
            maxExperiments: 2,
            config: DefaultScenarioConfig(),
            configureTarget: null,
            configurePipeline: (fixtures, pipeline) =>
            {
                ConfigureAnalysisSequence(fixtures, pipeline, "two-opportunities.json");
                ConfigureMetricFixtures(
                    fixtures,
                    pipeline,
                    (Experiment: 1, FixtureName: "regressed.json"),
                    (Experiment: 2, FixtureName: "regressed.json"));
                ConfigureRealMetricComparer(pipeline);
            },
            configureImplementer: null,
            assertScenario: run =>
            {
                _ = run.LoopResult.ExperimentsRun.Should().Be(2);
                _ = run.LoopResult.SuccessCount.Should().Be(0);
                _ = run.LoopResult.FailedExperiments.Should().Equal([1, 2]);
                _ = run.PullRequestCalls.Should().HaveCount(2);

                HarnessContractAssertions.AssertRunMetadataContracts(
                    run.RunMetadata,
                    "test-target",
                    [
                        new ExpectedExperimentContract(
                            Experiment: 1,
                            Outcome: ExperimentOutcome.Regressed,
                            BranchName: "hone/experiment-1",
                            BaseBranch: "main",
                            HasPullRequest: true,
                            HasMetrics: true),
                        new ExpectedExperimentContract(
                            Experiment: 2,
                            Outcome: ExperimentOutcome.Regressed,
                            BranchName: "hone/experiment-2",
                            BaseBranch: "main",
                            HasPullRequest: true,
                            HasMetrics: true),
                    ]);
                HarnessContractAssertions.AssertQueueContracts(
                    run.QueueJsonPath,
                    1,
                    [
                        new ExpectedQueueItemContract(
                            Id: "1",
                            FilePath: "src/Service1.cs",
                            Status: QueueItemStatus.Done,
                            TriedByExperiment: 1,
                            Outcome: "regressed"),
                        new ExpectedQueueItemContract(
                            Id: "2",
                            FilePath: "src/Service2.cs",
                            Status: QueueItemStatus.Done,
                            TriedByExperiment: 2,
                            Outcome: "regressed"),
                    ]);

                _ = File.Exists(Path.Combine(run.ResultsRoot, "experiment-1", "k6-summary.json")).Should().BeTrue();
            });

    private static HarnessLoopScenario BuildStaleScenario() =>
        new(
            name: "stale-outcome-limit",
            targetFixture: "regression",
            maxExperiments: 10,
            config: DefaultScenarioConfig(new TolerancesConfig(
                StaleExperimentsBeforeStop: 2,
                MaxConsecutiveFailures: 999)),
            configureTarget: null,
            configurePipeline: (fixtures, pipeline) =>
            {
                ConfigureAnalysisSequence(fixtures, pipeline, "two-opportunities.json");
                ConfigureMetricFixtures(
                    fixtures,
                    pipeline,
                    (Experiment: 1, FixtureName: "stale.json"),
                    (Experiment: 2, FixtureName: "stale.json"));
                ConfigureRealMetricComparer(pipeline);
            },
            configureImplementer: null,
            assertScenario: run =>
            {
                _ = run.LoopResult.ExitReason.Should().Be("stale_limit");
                _ = run.LoopResult.ExperimentsRun.Should().Be(2);
                _ = run.LoopResult.SuccessCount.Should().Be(0);
                _ = run.LoopResult.FailedExperiments.Should().Equal([1, 2]);

                HarnessContractAssertions.AssertRunMetadataContracts(
                    run.RunMetadata,
                    "test-target",
                    [
                        new ExpectedExperimentContract(
                            Experiment: 1,
                            Outcome: ExperimentOutcome.Stale,
                            BranchName: "hone/experiment-1",
                            BaseBranch: "main",
                            HasPullRequest: true,
                            HasMetrics: true),
                        new ExpectedExperimentContract(
                            Experiment: 2,
                            Outcome: ExperimentOutcome.Stale,
                            BranchName: "hone/experiment-2",
                            BaseBranch: "main",
                            HasPullRequest: true,
                            HasMetrics: true),
                    ]);
                HarnessContractAssertions.AssertQueueContracts(
                    run.QueueJsonPath,
                    1,
                    [
                        new ExpectedQueueItemContract(
                            Id: "1",
                            FilePath: "src/Service1.cs",
                            Status: QueueItemStatus.Done,
                            TriedByExperiment: 1,
                            Outcome: "stale"),
                        new ExpectedQueueItemContract(
                            Id: "2",
                            FilePath: "src/Service2.cs",
                            Status: QueueItemStatus.Done,
                            TriedByExperiment: 2,
                            Outcome: "stale"),
                    ]);
            });

    private static HarnessLoopScenario BuildQueueRefillScenario() =>
        new(
            name: "queue-refill-and-consumption",
            targetFixture: "happy-path",
            maxExperiments: 2,
            config: DefaultScenarioConfig(),
            configureTarget: null,
            configurePipeline: (fixtures, pipeline) =>
            {
                ConfigureAnalysisSequence(fixtures, pipeline, "single-opportunity-a.json", "single-opportunity-b.json");
                ConfigureMetricFixtures(
                    fixtures,
                    pipeline,
                    (Experiment: 1, FixtureName: "improved-step-1.json"),
                    (Experiment: 2, FixtureName: "improved-step-2.json"));
                ConfigureRealMetricComparer(pipeline);
            },
            configureImplementer: null,
            assertScenario: run =>
            {
                _ = run.LoopResult.ExperimentsRun.Should().Be(2);
                _ = run.LoopResult.SuccessCount.Should().Be(2);
                _ = run.AnalysisCallCount.Should().Be(2);

                HarnessContractAssertions.AssertRunMetadataContracts(
                    run.RunMetadata,
                    "test-target",
                    [
                        new ExpectedExperimentContract(
                            Experiment: 1,
                            Outcome: ExperimentOutcome.Improved,
                            BranchName: "hone/experiment-1",
                            BaseBranch: "main",
                            HasPullRequest: true,
                            HasMetrics: true),
                        new ExpectedExperimentContract(
                            Experiment: 2,
                            Outcome: ExperimentOutcome.Improved,
                            BranchName: "hone/experiment-2",
                            BaseBranch: "hone/experiment-1",
                            HasPullRequest: true,
                            HasMetrics: true),
                    ]);
                HarnessContractAssertions.AssertSuccessfulBranchLineage(run.RunMetadata);
                HarnessContractAssertions.AssertQueueContracts(
                    run.QueueJsonPath,
                    2,
                    [
                        new ExpectedQueueItemContract(
                            Id: "1",
                            FilePath: "src/Service2.cs",
                            Status: QueueItemStatus.Done,
                            TriedByExperiment: 2,
                            Outcome: "improved"),
                    ]);

                _ = run.PullRequestCalls.Should().HaveCount(2);
                _ = run.PullRequestCalls[0].Title.Should().Contain("src/Service1.cs");
                _ = run.PullRequestCalls[1].Title.Should().Contain("src/Service2.cs");
            });

    private static HarnessLoopScenario BuildStackedDiffsScenario() =>
        new(
            name: "stacked-diffs-success-failure-success",
            targetFixture: "stacked-diffs",
            maxExperiments: 3,
            config: SingleAttemptScenarioConfig(),
            configureTarget: null,
            configurePipeline: (fixtures, pipeline) =>
            {
                ConfigureAnalysisSequence(fixtures, pipeline, "three-opportunities.json");
                ConfigureMetricFixtures(
                    fixtures,
                    pipeline,
                    (Experiment: 1, FixtureName: "improved-step-1.json"),
                    (Experiment: 3, FixtureName: "improved-step-2.json"));
                ConfigureRealMetricComparer(pipeline);
            },
            configureImplementer: (_, implementer) =>
                ConfigureBuildFailures(implementer, failingExperiments: [2]),
            assertScenario: run =>
            {
                _ = run.LoopResult.ExperimentsRun.Should().Be(3);
                _ = run.LoopResult.SuccessCount.Should().Be(2);
                _ = run.LoopResult.FailedExperiments.Should().Equal([2]);
                _ = run.LoopResult.BranchChain.Should().Equal(["hone/experiment-1", "hone/experiment-3"]);

                HarnessContractAssertions.AssertRunMetadataContracts(
                    run.RunMetadata,
                    "test-target",
                    [
                        new ExpectedExperimentContract(
                            Experiment: 1,
                            Outcome: ExperimentOutcome.Improved,
                            BranchName: "hone/experiment-1",
                            BaseBranch: "main",
                            HasPullRequest: true,
                            HasMetrics: true),
                        new ExpectedExperimentContract(
                            Experiment: 2,
                            Outcome: ExperimentOutcome.BuildFailed,
                            BranchName: "hone/experiment-2",
                            BaseBranch: "hone/experiment-1",
                            HasPullRequest: true,
                            HasMetrics: false),
                        new ExpectedExperimentContract(
                            Experiment: 3,
                            Outcome: ExperimentOutcome.Improved,
                            BranchName: "hone/experiment-3",
                            BaseBranch: "hone/experiment-1",
                            HasPullRequest: true,
                            HasMetrics: true),
                    ]);
                HarnessContractAssertions.AssertSuccessfulBranchLineage(run.RunMetadata);
                HarnessContractAssertions.AssertQueueContracts(
                    run.QueueJsonPath,
                    1,
                    [
                        new ExpectedQueueItemContract(
                            Id: "1",
                            FilePath: "src/Service1.cs",
                            Status: QueueItemStatus.Done,
                            TriedByExperiment: 1,
                            Outcome: "improved"),
                        new ExpectedQueueItemContract(
                            Id: "2",
                            FilePath: "src/Service2.cs",
                            Status: QueueItemStatus.Done,
                            TriedByExperiment: 2,
                            Outcome: "build_failed"),
                        new ExpectedQueueItemContract(
                            Id: "3",
                            FilePath: "src/Service3.cs",
                            Status: QueueItemStatus.Done,
                            TriedByExperiment: 3,
                            Outcome: "improved"),
                    ]);

                _ = run.PullRequestCalls.Should().HaveCount(3);
                _ = run.PullRequestCalls[0].BaseBranch.Should().Be("main");
                _ = run.PullRequestCalls[1].BaseBranch.Should().Be("hone/experiment-1");
                _ = run.PullRequestCalls[2].BaseBranch.Should().Be("hone/experiment-1");
            });

    private static HarnessLoopScenario BuildResumeScenario() =>
        new(
            name: "resume-from-partial-state",
            targetFixture: "happy-path",
            maxExperiments: 1,
            config: DefaultScenarioConfig(),
            configureTarget: (_, targetDir) =>
            {
                RunMetadata seededMetadata = new(
                    TargetName: "test-target",
                    StartedAt: "2024-01-01T00:00:00Z",
                    MachineInfo: TestMachine,
                    Experiments:
                    [
                        new ExperimentMetadata(
                            Experiment: 1,
                            StartedAt: "2024-01-01T00:00:00Z",
                            CompletedAt: "2024-01-01T00:10:00Z",
                            Outcome: ExperimentOutcome.Improved,
                            BranchName: "hone/experiment-1",
                            BaseBranch: "main",
                            P95: 130,
                            RPS: 550,
                            PrNumber: 101,
                            PrUrl: new Uri("https://github.com/test/pr/101"),
                            StaleCount: 0,
                            ConsecutiveFailures: 0),
                        new ExperimentMetadata(
                            Experiment: 2,
                            StartedAt: "2024-01-01T00:10:00Z",
                            CompletedAt: "2024-01-01T00:20:00Z",
                            Outcome: ExperimentOutcome.BuildFailed,
                            BranchName: "hone/experiment-2",
                            BaseBranch: "hone/experiment-1",
                            P95: null,
                            RPS: null,
                            PrNumber: 102,
                            PrUrl: new Uri("https://github.com/test/pr/102"),
                            StaleCount: 0,
                            ConsecutiveFailures: 1),
                    ]);

                WriteJson(Path.Combine(targetDir, "hone-results", "run-metadata.json"), seededMetadata);
            },
            configurePipeline: (fixtures, pipeline) =>
            {
                ConfigureAnalysisSequence(fixtures, pipeline, "single-opportunity-c.json");
                ConfigureMetricFixtures(
                    fixtures,
                    pipeline,
                    (Experiment: 3, FixtureName: "improved-step-2.json"));
                ConfigureRealMetricComparer(pipeline);
            },
            configureImplementer: null,
            assertScenario: run =>
            {
                _ = run.LoopResult.ExperimentsRun.Should().Be(1);
                _ = run.LoopResult.SuccessCount.Should().Be(2);
                _ = run.LoopResult.FailedExperiments.Should().Equal([2]);

                HarnessContractAssertions.AssertRunMetadataContracts(
                    run.RunMetadata,
                    "test-target",
                    [
                        new ExpectedExperimentContract(
                            Experiment: 1,
                            Outcome: ExperimentOutcome.Improved,
                            BranchName: "hone/experiment-1",
                            BaseBranch: "main",
                            HasPullRequest: true,
                            HasMetrics: true),
                        new ExpectedExperimentContract(
                            Experiment: 2,
                            Outcome: ExperimentOutcome.BuildFailed,
                            BranchName: "hone/experiment-2",
                            BaseBranch: "hone/experiment-1",
                            HasPullRequest: true,
                            HasMetrics: false),
                        new ExpectedExperimentContract(
                            Experiment: 3,
                            Outcome: ExperimentOutcome.Improved,
                            BranchName: "hone/experiment-3",
                            BaseBranch: "hone/experiment-1",
                            HasPullRequest: true,
                            HasMetrics: true),
                    ]);
                HarnessContractAssertions.AssertSuccessfulBranchLineage(run.RunMetadata);
                HarnessContractAssertions.AssertQueueContracts(
                    run.QueueJsonPath,
                    3,
                    [
                        new ExpectedQueueItemContract(
                            Id: "1",
                            FilePath: "src/Service3.cs",
                            Status: QueueItemStatus.Done,
                            TriedByExperiment: 3,
                            Outcome: "improved"),
                    ]);
            });

    private static HarnessLoopScenario BuildStartFailureScenario() =>
        new(
            name: "start-hook-failure",
            targetFixture: "happy-path",
            maxExperiments: 1,
            config: DefaultScenarioConfig(),
            configureTarget: null,
            configurePipeline: (fixtures, pipeline) =>
            {
                ConfigureAnalysisSequence(fixtures, pipeline, "single-opportunity-a.json");
                ConfigureStartFailures(pipeline, failingExperiments: [1]);
            },
            configureImplementer: null,
            assertScenario: run =>
            {
                _ = run.LoopResult.ExperimentsRun.Should().Be(1);
                _ = run.LoopResult.SuccessCount.Should().Be(0);
                _ = run.LoopResult.FailedExperiments.Should().Equal([1]);

                HarnessContractAssertions.AssertRunMetadataContracts(
                    run.RunMetadata,
                    "test-target",
                    [
                        new ExpectedExperimentContract(
                            Experiment: 1,
                            Outcome: ExperimentOutcome.StartFailed,
                            BranchName: "hone/experiment-1",
                            BaseBranch: "main",
                            HasPullRequest: true,
                            HasMetrics: false),
                    ]);
                HarnessContractAssertions.AssertQueueContracts(
                    run.QueueJsonPath,
                    1,
                    [
                        new ExpectedQueueItemContract(
                            Id: "1",
                            FilePath: "src/Service1.cs",
                            Status: QueueItemStatus.Done,
                            TriedByExperiment: 1,
                            Outcome: "start_failed"),
                    ]);
            });

    private static HarnessLoopScenario BuildLoadTestFailureScenario() =>
        new(
            name: "load-test-failure",
            targetFixture: "happy-path",
            maxExperiments: 1,
            config: DefaultScenarioConfig(),
            configureTarget: null,
            configurePipeline: (fixtures, pipeline) =>
            {
                ConfigureAnalysisSequence(fixtures, pipeline, "single-opportunity-a.json");
                ConfigureLoadTestFailures(pipeline, failingExperiments: [1]);
            },
            configureImplementer: null,
            assertScenario: run =>
            {
                _ = run.LoopResult.ExperimentsRun.Should().Be(1);
                _ = run.LoopResult.SuccessCount.Should().Be(0);
                _ = run.LoopResult.FailedExperiments.Should().Equal([1]);

                HarnessContractAssertions.AssertRunMetadataContracts(
                    run.RunMetadata,
                    "test-target",
                    [
                        new ExpectedExperimentContract(
                            Experiment: 1,
                            Outcome: ExperimentOutcome.LoadTestFailed,
                            BranchName: "hone/experiment-1",
                            BaseBranch: "main",
                            HasPullRequest: true,
                            HasMetrics: false),
                    ]);
                HarnessContractAssertions.AssertQueueContracts(
                    run.QueueJsonPath,
                    1,
                    [
                        new ExpectedQueueItemContract(
                            Id: "1",
                            FilePath: "src/Service1.cs",
                            Status: QueueItemStatus.Done,
                            TriedByExperiment: 1,
                            Outcome: "load_test_failed"),
                    ]);

                _ = File.Exists(Path.Combine(run.ResultsRoot, "experiment-1", "k6-summary.json")).Should().BeFalse();
                _ = File.Exists(Path.Combine(run.ResultsRoot, "experiment-1", "k6.log")).Should().BeTrue();
            });

    private static HoneConfig DefaultScenarioConfig(TolerancesConfig? tolerances = null) =>
        new(
            Loop: new LoopConfig(SkipClassification: true, ExperimentCooldownSeconds: 0),
            Tolerances: tolerances);

    private static HoneConfig SingleAttemptScenarioConfig(TolerancesConfig? tolerances = null) =>
        new(
            Loop: new LoopConfig(SkipClassification: true, ExperimentCooldownSeconds: 0),
            Tolerances: tolerances,
            Implementer: new ImplementerConfig(MaxAttempts: 1));

    private static void ConfigureAnalysisSequence(
        HarnessFixtureCatalog fixtures,
        ILoopPipeline pipeline,
        params string[] fixtureNames)
    {
        var remaining = new Queue<IReadOnlyList<Opportunity>>(
            fixtureNames.Select(fixtures.LoadOpportunities));

        _ = pipeline.RunAnalysisAsync(Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (remaining.Count == 0)
                {
                    throw new InvalidOperationException("Analysis was invoked more times than the scenario defined.");
                }

                return Task.FromResult(new AnalysisResult(
                    Success: true,
                    Opportunities: remaining.Dequeue()));
            });
    }

    private static void ConfigureMetricFixtures(
        HarnessFixtureCatalog fixtures,
        ILoopPipeline pipeline,
        params (int Experiment, string FixtureName)[] metricFixtures)
    {
        Dictionary<int, MetricSet> metricsByExperiment = metricFixtures.ToDictionary(
            item => item.Experiment,
            item => fixtures.LoadMetricSet(item.FixtureName));

        _ = pipeline.RunLoadTestAsync(Arg.Any<LoadTestInput>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                LoadTestInput input = callInfo.Arg<LoadTestInput>();
                if (!metricsByExperiment.TryGetValue(input.Experiment, out MetricSet? metrics))
                {
                    throw new InvalidOperationException(
                        $"No metric fixture configured for experiment {input.Experiment}.");
                }

                string experimentDir = Path.Combine(
                    input.TargetDir, input.ResultsPath, $"experiment-{input.Experiment}");
                Directory.CreateDirectory(experimentDir);

                string summaryPath = Path.Combine(experimentDir, "k6-summary.json");
                MetricSet materialized = metrics with
                {
                    Experiment = input.Experiment,
                    SummaryPath = summaryPath,
                };
                WriteJson(summaryPath, materialized);

                return Task.FromResult(new LoadTestResult(
                    Success: true,
                    Metrics: materialized,
                    SummaryPath: summaryPath,
                    Output: null));
            });
    }

    private static void ConfigureRealMetricComparer(ILoopPipeline pipeline)
    {
        _ = pipeline.CompareMetrics(
                Arg.Any<MetricSet>(),
                Arg.Any<MetricSet>(),
                Arg.Any<MetricSet?>(),
                Arg.Any<int>(),
                Arg.Any<HoneConfig>())
            .Returns(callInfo =>
            {
                MetricSet current = callInfo.ArgAt<MetricSet>(0);
                MetricSet baseline = callInfo.ArgAt<MetricSet>(1);
                MetricSet? previous = callInfo.ArgAt<MetricSet?>(2);
                HoneConfig config = callInfo.ArgAt<HoneConfig>(4);

                return MetricComparer.Compare(
                    current,
                    previous ?? baseline,
                    baseline,
                    config.Tolerances);
            });
    }

    private static void ConfigureBuildFailures(
        IImplementerPipeline implementer,
        params int[] failingExperiments)
    {
        HashSet<int> failingSet = [.. failingExperiments];

        _ = implementer.BuildProjectAsync(Arg.Any<BuildStepInput>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                BuildStepInput input = callInfo.Arg<BuildStepInput>();
                if (failingSet.Contains(input.Experiment))
                {
                    WriteText(input.AdditionalLogPath, $"Build failed for experiment {input.Experiment}");
                    return Task.FromResult(new BuildStepResult(
                        Success: false,
                        Output: $"Build failed for experiment {input.Experiment}"));
                }

                WriteText(input.AdditionalLogPath, $"Build passed for experiment {input.Experiment}");
                return Task.FromResult(new BuildStepResult(Success: true, Output: null));
            });
    }

    private static void ConfigureTestFailures(
        IImplementerPipeline implementer,
        params int[] failingExperiments)
    {
        HashSet<int> failingSet = [.. failingExperiments];

        _ = implementer.RunTestsAsync(Arg.Any<TestStepInput>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                TestStepInput input = callInfo.Arg<TestStepInput>();
                WriteText(input.AdditionalTrxPath, "<TestRun />");

                if (failingSet.Contains(input.Experiment))
                {
                    WriteText(input.AdditionalLogPath, $"Tests failed for experiment {input.Experiment}");
                    return Task.FromResult(new TestStepResult(
                        Success: false,
                        Output: $"Tests failed for experiment {input.Experiment}"));
                }

                WriteText(input.AdditionalLogPath, $"Tests passed for experiment {input.Experiment}");
                return Task.FromResult(new TestStepResult(Success: true, Output: null));
            });
    }

    private static void ConfigureStartFailures(
        ILoopPipeline pipeline,
        params int[] failingExperiments)
    {
        HashSet<int> failingSet = [.. failingExperiments];

        _ = pipeline.StartTargetAsync(
                Arg.Any<string>(), Arg.Any<HoneConfig>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                int experiment = callInfo.ArgAt<int>(2);
                bool failing = failingSet.Contains(experiment);

                return Task.FromResult(new HookResult(
                    Success: !failing,
                    Message: failing ? "Start hook failed" : "Started",
                    Duration: TimeSpan.FromSeconds(1),
                    Artifacts: [],
                    BaseUrl: failing ? null : new Uri("http://localhost:5050")));
            });
    }

    private static void ConfigureLoadTestFailures(
        ILoopPipeline pipeline,
        params int[] failingExperiments)
    {
        HashSet<int> failingSet = [.. failingExperiments];

        _ = pipeline.RunLoadTestAsync(Arg.Any<LoadTestInput>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                LoadTestInput input = callInfo.Arg<LoadTestInput>();
                string experimentDir = Path.Combine(
                    input.TargetDir, input.ResultsPath, $"experiment-{input.Experiment}");
                Directory.CreateDirectory(experimentDir);
                string logPath = Path.Combine(experimentDir, "k6.log");
                WriteText(logPath, $"Load test failed for experiment {input.Experiment}");

                bool failing = failingSet.Contains(input.Experiment);
                return Task.FromResult(new LoadTestResult(
                    Success: !failing,
                    Metrics: null,
                    SummaryPath: null,
                    Output: failing ? $"Load test failed for experiment {input.Experiment}" : null));
            });
    }

    private static void WriteJson<T>(string path, T payload)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(payload, MetadataJsonOptions);
#pragma warning disable CA1849, RS0030 // Test helper intentionally uses sync I/O for deterministic setup
        File.WriteAllText(path, json);
#pragma warning restore CA1849, RS0030
    }

    private static void WriteText(string? path, string content)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

#pragma warning disable CA1849, RS0030 // Test helper intentionally uses sync I/O for deterministic setup
        File.WriteAllText(path, content);
#pragma warning restore CA1849, RS0030
    }

    private sealed class HarnessLoopScenario(
        string name,
        string targetFixture,
        int maxExperiments,
        HoneConfig config,
        Action<HarnessFixtureCatalog, string>? configureTarget,
        Action<HarnessFixtureCatalog, ILoopPipeline>? configurePipeline,
        Action<HarnessFixtureCatalog, IImplementerPipeline>? configureImplementer,
        Action<HarnessScenarioRunResult> assertScenario)
    {
        public string Name { get; } = name;

        public string TargetFixture { get; } = targetFixture;

        public int MaxExperiments { get; } = maxExperiments;

        public HoneConfig Config { get; } = config;

        public bool DryRun { get; init; }

        public Action<HarnessFixtureCatalog, string>? ConfigureTarget { get; } = configureTarget;

        public Action<HarnessFixtureCatalog, ILoopPipeline>? ConfigurePipeline { get; } = configurePipeline;

        public Action<HarnessFixtureCatalog, IImplementerPipeline>? ConfigureImplementer { get; } = configureImplementer;

        public Action<HarnessScenarioRunResult> Assert { get; } = assertScenario;

        public override string ToString() => Name;
    }

    private sealed record HarnessScenarioRunResult(
        HarnessLoopScenario Scenario,
        TestHarness Harness,
        LoopResult LoopResult,
        RunMetadata RunMetadata,
        string MetadataPath,
        string QueueJsonPath,
        IReadOnlyList<HoneEvent> EmittedEvents)
    {
        public string ResultsRoot => Path.Combine(Harness.TargetDir, "hone-results");

        public int AnalysisCallCount =>
            Harness.Pipeline
                .ReceivedCalls()
                .Count(call => string.Equals(
                    call.GetMethodInfo().Name,
                    nameof(ILoopPipeline.RunAnalysisAsync),
                    StringComparison.Ordinal));

        public IReadOnlyList<CreatePrOptions> PullRequestCalls =>
        [
            .. Harness.Pipeline
                .ReceivedCalls()
                .Where(call => string.Equals(
                    call.GetMethodInfo().Name,
                    nameof(ILoopPipeline.CreatePullRequestAsync),
                    StringComparison.Ordinal))
                .Select(call => (CreatePrOptions)call.GetArguments()[0]!),
        ];
    }
}
