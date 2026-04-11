using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Orchestration.Failure;
using Hone.Orchestration.Implementer;
using Hone.Orchestration.Loop;
using Hone.Orchestration.Queue;
using Hone.TestInfrastructure;
using Hone.TestInfrastructure.HarnessTesting;
using NSubstitute;
using Xunit.Abstractions;

namespace Hone.Integration.Tests;

/// <summary>
/// Base class for integration tests exercising the full HoneLoopRunner pipeline
/// with mocked ILoopPipeline and IImplementerPipeline.
/// </summary>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit requires public test classes")]
public abstract class IntegrationTestBase(ITestOutputHelper output) : HoneTestBase(output)
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        WriteIndented = true,
    };

    // ── Shared metric / comparison factories ─────────────────────────────────

    protected static readonly MetricSet BaselineMetrics = new(
        Timestamp: "2024-01-01T00:00:00Z",
        Experiment: 0,
        Run: 1,
        HttpReqDuration: new HttpReqDurationMetrics(
            Avg: 100, P50: 90, P90: 140, P95: 150, P99: 180, Max: 250),
        HttpReqs: new HttpReqCountMetrics(Count: 10000, Rate: 500),
        HttpReqFailed: new HttpReqFailedMetrics(Count: 0, Rate: 0),
        SummaryPath: null);

    protected static readonly MachineInfo TestMachine = new(
        CpuName: "Test CPU",
        CpuCores: 8,
        TotalRamGB: 32,
        OsVersion: "Test OS",
        DotnetVersion: "10.0.0");

    protected static MetricSet MakeImprovedMetrics(int experiment) => new(
        Timestamp: DateTimeOffset.UtcNow.ToString("o"),
        Experiment: experiment,
        Run: 1,
        HttpReqDuration: new HttpReqDurationMetrics(
            Avg: 90, P50: 80, P90: 125, P95: 130, P99: 160, Max: 220),
        HttpReqs: new HttpReqCountMetrics(Count: 11000, Rate: 550),
        HttpReqFailed: new HttpReqFailedMetrics(Count: 0, Rate: 0),
        SummaryPath: null);

    protected static ComparisonResult ImprovedComparison() => new(
        Accepted: true,
        Outcome: ExperimentOutcome.Improved,
        ImprovementPct: 0.13,
        RegressionPct: 0,
        Details: []);

    protected static ComparisonResult RegressedComparison() => new(
        Accepted: false,
        Outcome: ExperimentOutcome.Regressed,
        ImprovementPct: 0,
        RegressionPct: 0.20,
        Details: []);

    protected static ComparisonResult StaleComparison() => new(
        Accepted: false,
        Outcome: ExperimentOutcome.Stale,
        ImprovementPct: 0.001,
        RegressionPct: 0,
        Details: []);

    protected static IReadOnlyList<Opportunity> MakeOpportunities(int count = 2)
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

    private protected static FixStepResult SucceededFix() =>
        new(Success: true, CodeBlock: "// fixed",
            PromptPath: null, ResponsePath: null,
            AttemptPromptPath: null, AttemptResponsePath: null);

    private protected static FixStepResult FailedFix() =>
        new(Success: false, CodeBlock: null,
            PromptPath: null, ResponsePath: null,
            AttemptPromptPath: null, AttemptResponsePath: null);

    // ── Harness creation ─────────────────────────────────────────────────────

    private protected TestHarness CreateHarness(
        Action<ILoopPipeline>? configurePipeline = null,
        Action<IImplementerPipeline>? configureImplementer = null,
        HoneConfig? config = null,
        string? targetFixture = null,
        Action<string>? configureTarget = null)
    {
        string targetDir = string.IsNullOrEmpty(targetFixture)
            ? CreateTargetDir("target", b =>
            {
                _ = b.AddFile("src/Service1.cs", "// original code 1");
                _ = b.AddFile("src/Service2.cs", "// original code 2");
                _ = b.AddFile("src/Service3.cs", "// original code 3");
            })
            : CopyFixtureTarget(Path.Combine("harness-testing", "targets", targetFixture));

        configureTarget?.Invoke(targetDir);

        string metadataDir = Path.Combine(targetDir, "hone-results", "metadata");
        Directory.CreateDirectory(metadataDir);

        IHoneEventSink eventSink = Substitute.For<IHoneEventSink>();
        IVersionControl versionControl = Substitute.For<IVersionControl>();

        // Queue manager (real, filesystem-backed)
        var queueManager = new OptimizationQueueManager(metadataDir, eventSink);

        // Implementer pipeline mock — sensible defaults
        IImplementerPipeline implPipeline = Substitute.For<IImplementerPipeline>();
        _ = implPipeline.InvokeImplementerAgentAsync(Arg.Any<FixStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SucceededFix()));
        _ = implPipeline.ApplySuggestionAsync(Arg.Any<ApplyStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ApplyStepResult(Success: true, CommitSha: "abc123", Description: "fix")));
        _ = implPipeline.GetDiffLineCountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(5));
        _ = implPipeline.BuildProjectAsync(Arg.Any<BuildStepInput>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                BuildStepInput input = callInfo.Arg<BuildStepInput>();
                await WriteTextIfRequestedAsync(input.AdditionalLogPath, "Build succeeded").ConfigureAwait(false);
                return new BuildStepResult(Success: true, Output: null);
            });
        _ = implPipeline.RunTestsAsync(Arg.Any<TestStepInput>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                TestStepInput input = callInfo.Arg<TestStepInput>();
                await WriteTextIfRequestedAsync(input.AdditionalLogPath, "Tests passed").ConfigureAwait(false);
                await WriteTextIfRequestedAsync(input.AdditionalTrxPath, "<TestRun />").ConfigureAwait(false);
                return new TestStepResult(Success: true, Output: null);
            });
        _ = implPipeline.GetChangedFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["src/Service1.cs"]));
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
                Arg.Any<string>(), Arg.Any<HoneConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BaselineMetrics));
        _ = pipeline.GetMachineInfoAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TestMachine));
        _ = pipeline.LoadRunMetadataAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                string path = callInfo.ArgAt<string>(0);
                if (!File.Exists(path))
                {
                    return null;
                }

                string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                return JsonSerializer.Deserialize<RunMetadata>(json);
            });
        _ = pipeline.SaveRunMetadataAsync(
                Arg.Any<string>(), Arg.Any<RunMetadata>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                string path = callInfo.ArgAt<string>(0);
                RunMetadata metadata = callInfo.ArgAt<RunMetadata>(1);
                string? directory = Path.GetDirectoryName(path);
                if (directory is not null)
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonSerializer.Serialize(metadata, MetadataJsonOptions);
                await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            });
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
                Arg.Any<string>(), Arg.Any<HoneConfig>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HookResult(
                Success: true, Message: "Warmed up", Duration: TimeSpan.Zero,
                Artifacts: [], BaseUrl: null)));
        _ = pipeline.CooldownAsync(
                Arg.Any<string>(), Arg.Any<HoneConfig>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HookResult(
                Success: true, Message: "Cooled down", Duration: TimeSpan.Zero,
                Artifacts: [], BaseUrl: null)));
        _ = pipeline.CleanupAsync(
                Arg.Any<string>(), Arg.Any<HoneConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HookResult(
                Success: true, Message: "Cleaned up", Duration: TimeSpan.Zero,
                Artifacts: [], BaseUrl: null)));
    }

    private protected static HarnessFixtureCatalog CreateHarnessFixtureCatalog() =>
        new(Path.Combine(TestFixturesRootPath, "harness-testing"));

    private static async Task WriteTextIfRequestedAsync(string? path, string content)
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

        await File.WriteAllTextAsync(path, content).ConfigureAwait(false);
    }

    // ── Test harness record ──────────────────────────────────────────────────

    private protected sealed record TestHarness(
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
}
