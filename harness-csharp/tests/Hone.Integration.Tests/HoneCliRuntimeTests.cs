using System.Net;
using System.Text;

using FluentAssertions;

using Hone.Cli;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Lifecycle.Hooks;
using Hone.Lifecycle.SharedHooks;
using Hone.Lifecycle.Validation;
using Hone.Orchestration.Loop;
using Hone.TestInfrastructure;

using NSubstitute;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Integration.Tests;

public sealed class HoneCliRuntimeTests(ITestOutputHelper output)
    : HoneTestBase(output)
{
    [Fact]
    public void LoadTargetConfig_LoadsSampleStyleYaml()
    {
        string targetDir = CreateTargetDir("load-target-config", builder =>
            _ = builder.AddFile(".hone\\config.yaml", """
Name: "LoaderTarget"
BaseBranch: "master"
Api:
  SolutionPath: "MyApp.sln"
  ProjectPath: "MyApp"
ScaleTest:
  ScenarioPath: '.hone\scenarios\baseline.js'
Hooks:
  Prepare:
    Type: Skip
  Build:
    Type: BuiltIn
    Name: dotnet-build
  Test:
    Type: BuiltIn
    Name: dotnet-test
  Start:
    Type: BuiltIn
    Name: dotnet-start
  Stop:
    Type: BuiltIn
    Name: dotnet-stop
  Ready:
    Type: BuiltIn
    Name: health-poll
  Warmup:
    Type: Skip
  Active:
    Type: BuiltIn
    Name: k6-run
  Cooldown:
    Type: Http
    Method: POST
    Path: "/diag/gc"
  Cleanup:
    Type: Skip
"""));

        string configPath = Path.Combine(targetDir, ".hone", "config.yaml");

        TargetConfig targetConfig = Program.LoadTargetConfig(configPath);

        _ = targetConfig.Name.Should().Be("LoaderTarget");
        _ = targetConfig.BaseBranch.Should().Be("master");
        _ = targetConfig.Hooks.Should().ContainKey("Build");
        _ = targetConfig.Hooks.Should().ContainKey("Test");
        _ = targetConfig.Hooks["Cooldown"].Method.Should().Be("POST");
        _ = targetConfig.Hooks["Cooldown"].Path.Should().Be("/diag/gc");
    }

    [Fact]
    public void ValidateConfiguration_MissingBuildAndTestHooks_Fails()
    {
        string targetDir = CreateTargetDir("validate-target", builder =>
        {
            _ = builder.AddFile("MyApp.sln", "");
            _ = builder.AddFile("MyApp\\app.csproj", "<Project />");
            _ = builder.AddFile("MyApp.Tests\\tests.csproj", "<Project />");
            _ = builder.AddFile(".hone\\scenarios\\baseline.js", "export default function () {}");
        });

        HoneConfig config = new(
            Api: new ApiConfig(
                SolutionPath: "MyApp.sln",
                ProjectPath: "MyApp",
                TestProjectPath: "MyApp.Tests",
                BaseUrl: "http://localhost:0",
                ResultsPath: "hone-results",
                MetadataPath: "hone-results\\metadata"),
            ScaleTest: new ScaleTestConfig(
                ScenarioPath: ".hone\\scenarios\\baseline.js",
                ScenarioRegistryPath: null));
        TargetConfig targetConfig = new(
            Name: "ValidateTarget",
            Hooks: new Dictionary<string, TargetHookConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["Prepare"] = new("Skip"),
                ["Start"] = new("BuiltIn", Name: "dotnet-start"),
                ["Stop"] = new("BuiltIn", Name: "dotnet-stop"),
                ["Ready"] = new("BuiltIn", Name: "health-poll"),
                ["Warmup"] = new("Skip"),
                ["Active"] = new("BuiltIn", Name: "k6-run"),
                ["Cooldown"] = new("Skip"),
                ["Cleanup"] = new("Skip"),
            });

        ValidationResult result = Program.ValidateConfiguration(config, targetConfig, targetDir);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Errors.Should().Contain(error => error.Contains("Hooks.Build is not declared", StringComparison.Ordinal));
        _ = result.Errors.Should().Contain(error => error.Contains("Hooks.Test is not declared", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunLoadTestAsync_UsesRuntimeBaseUrlForMeasurementAndCooldown()
    {
        string targetDir = CreateTargetDir("adapter-run-target", builder =>
        {
            _ = builder.AddFile("baseline.js", "export default function () {}");
            _ = builder.AddFile("warmup.js", "export default function () {}");
        });

        var observedBaseUrls = new List<Uri>();
        var capturedRequests = new List<Uri>();
        Uri runtimeBaseUrl = new("http://127.0.0.1:60123");

        ILoadTestRunner loadTestRunner = Substitute.For<ILoadTestRunner>();
        _ = loadTestRunner.RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                LoadTestOptions options = callInfo.Arg<LoadTestOptions>();
                observedBaseUrls.Add(options.BaseUrl);

                if (options.Run == 0)
                {
                    return Task.FromResult(new LoadTestResult(
                        Success: true,
                        Metrics: null,
                        SummaryPath: null,
                        Output: null));
                }

                return Task.FromResult(new LoadTestResult(
                    Success: true,
                    Metrics: CreateMetrics(options.Experiment, options.Run, summaryPath: null),
                    SummaryPath: null,
                    Output: null));
            });

        using var httpHandler = new CapturingHttpMessageHandler(capturedRequests);
        using var httpClient = new HttpClient(httpHandler);
        IProcessRunner processRunner = Substitute.For<IProcessRunner>();
        var hookRegistry = new BuiltInHookRegistry(
            new DotnetBuildHook(processRunner),
            new DotnetTestHook(processRunner),
            new DotnetStartHook(httpClient),
            new DotnetStopHook(),
            new HealthPollHook(httpClient),
            new K6RunHook(loadTestRunner));
        var hookDispatcher = new LifecycleHookDispatcher(hookRegistry, processRunner, httpClient);

        HoneConfig config = CreateConfig(
            baseUrl: "http://localhost:0",
            resultsPath: "hone-results",
            warmupEnabled: true);
        TargetConfig targetConfig = new(
            Name: "AdapterTarget",
            Hooks: new Dictionary<string, TargetHookConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["Warmup"] = new("Skip"),
                ["Cooldown"] = new("Http", Path: "/diag/gc", Method: "POST"),
            });

        var adapter = new LoopPipelineAdapter(
            loadTestRunner,
            analysisAgent: null!,
            classificationAgent: null!,
            codeHost: null!,
            versionControl: null!,
            config,
            httpClient,
            hookDispatcher,
            targetConfig);

        LoadTestResult result = await adapter.RunLoadTestAsync(
            new LoadTestInput(targetDir, 1, "hone-results", runtimeBaseUrl),
            CancellationToken.None);

        _ = result.Success.Should().BeTrue();
        _ = observedBaseUrls.Should().NotBeEmpty();
        _ = observedBaseUrls.Should().OnlyContain(uri => uri == runtimeBaseUrl);
        _ = capturedRequests.Should().ContainSingle(uri => uri == new Uri(runtimeBaseUrl, "/diag/gc"));
    }

    [Fact]
    public async Task RunLoadTestAsync_CooldownTimeout_DoesNotAbortMeasurement()
    {
        string targetDir = CreateTargetDir("adapter-run-timeout-target", builder =>
        {
            _ = builder.AddFile("baseline.js", "export default function () {}");
            _ = builder.AddFile("warmup.js", "export default function () {}");
        });

        string sourceSummaryPath = Path.Combine(targetDir, "selected-summary-timeout.json");
        await File.WriteAllTextAsync(sourceSummaryPath, "{\"summary\":true}");

        var observedBaseUrls = new List<Uri>();
        var capturedRequests = new List<Uri>();
        Uri runtimeBaseUrl = new("http://127.0.0.1:60124");

        ILoadTestRunner loadTestRunner = Substitute.For<ILoadTestRunner>();
        _ = loadTestRunner.RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                LoadTestOptions options = callInfo.Arg<LoadTestOptions>();
                observedBaseUrls.Add(options.BaseUrl);

                return Task.FromResult(new LoadTestResult(
                    Success: true,
                    Metrics: CreateMetrics(options.Experiment, options.Run, sourceSummaryPath),
                    SummaryPath: sourceSummaryPath,
                    Output: null));
            });

        using var httpHandler = new TimeoutHttpMessageHandler(capturedRequests);
        using var httpClient = new HttpClient(httpHandler);
        IProcessRunner processRunner = Substitute.For<IProcessRunner>();
        var hookRegistry = new BuiltInHookRegistry(
            new DotnetBuildHook(processRunner),
            new DotnetTestHook(processRunner),
            new DotnetStartHook(httpClient),
            new DotnetStopHook(),
            new HealthPollHook(httpClient),
            new K6RunHook(loadTestRunner));
        var hookDispatcher = new LifecycleHookDispatcher(hookRegistry, processRunner, httpClient);

        HoneConfig config = CreateConfig(
            baseUrl: "http://localhost:0",
            resultsPath: "hone-results",
            warmupEnabled: true);
        TargetConfig targetConfig = new(
            Name: "AdapterTarget",
            Hooks: new Dictionary<string, TargetHookConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["Warmup"] = new("Skip"),
                ["Cooldown"] = new("Http", Path: "/diag/gc", Method: "POST"),
            });

        var adapter = new LoopPipelineAdapter(
            loadTestRunner,
            analysisAgent: null!,
            classificationAgent: null!,
            codeHost: null!,
            versionControl: null!,
            config,
            httpClient,
            hookDispatcher,
            targetConfig);

        LoadTestResult result = await adapter.RunLoadTestAsync(
            new LoadTestInput(targetDir, 1, "hone-results", runtimeBaseUrl),
            CancellationToken.None);

        _ = result.Success.Should().BeTrue();
        _ = result.Metrics.Should().NotBeNull();
        _ = observedBaseUrls.Should().NotBeEmpty();
        _ = observedBaseUrls.Should().OnlyContain(uri => uri == runtimeBaseUrl);
        _ = capturedRequests.Should().ContainSingle(uri => uri == new Uri(runtimeBaseUrl, "/diag/gc"));
    }

    [Fact]
    public async Task LoadOrCreateBaselineAsync_UsesRuntimeBaseUrlWhenCreatingBaseline()
    {
        string targetDir = CreateTargetDir(
            "adapter-baseline-target",
            builder => _ = builder.AddFile("baseline.js", "export default function () {}"));

        string sourceSummaryPath = Path.Combine(targetDir, "selected-summary.json");
        await File.WriteAllTextAsync(sourceSummaryPath, "{\"summary\":true}");

        var observedBaseUrls = new List<Uri>();
        Uri runtimeBaseUrl = new("http://127.0.0.1:61234");

        ILoadTestRunner loadTestRunner = Substitute.For<ILoadTestRunner>();
        _ = loadTestRunner.RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                LoadTestOptions options = callInfo.Arg<LoadTestOptions>();
                observedBaseUrls.Add(options.BaseUrl);

                return Task.FromResult(new LoadTestResult(
                    Success: true,
                    Metrics: CreateMetrics(options.Experiment, options.Run, sourceSummaryPath),
                    SummaryPath: sourceSummaryPath,
                    Output: null));
            });

        HoneConfig config = CreateConfig(
            baseUrl: "http://localhost:0",
            resultsPath: "hone-results",
            warmupEnabled: false);

        var adapter = new LoopPipelineAdapter(
            loadTestRunner,
            analysisAgent: null!,
            classificationAgent: null!,
            codeHost: null!,
            versionControl: null!,
            config);

        MetricSet baseline = await adapter.LoadOrCreateBaselineAsync(
            targetDir,
            config,
            runtimeBaseUrl,
            CancellationToken.None);

        string persistedSummaryPath = Path.Combine(targetDir, "hone-results", "baseline", "k6-summary.json");

        _ = baseline.HttpReqDuration.P95.Should().Be(125);
        _ = observedBaseUrls.Should().ContainSingle(uri => uri == runtimeBaseUrl);
        _ = File.Exists(persistedSummaryPath).Should().BeTrue();
        string persistedSummary = await File.ReadAllTextAsync(persistedSummaryPath);
        _ = persistedSummary.Should().Be("{\"summary\":true}");
    }

    [Fact]
    public async Task SaveRunMetadataAsync_WritesMetadataWithoutLeavingTemporaryFile()
    {
        string targetDir = CreateTargetDir("adapter-run-metadata-target");
        HoneConfig config = CreateConfig(
            baseUrl: "http://localhost:0",
            resultsPath: "hone-results",
            warmupEnabled: false);
        ILoadTestRunner loadTestRunner = Substitute.For<ILoadTestRunner>();
        var adapter = new LoopPipelineAdapter(
            loadTestRunner,
            analysisAgent: null!,
            classificationAgent: null!,
            codeHost: null!,
            versionControl: null!,
            config);

        string metadataPath = Path.Combine(targetDir, "hone-results", "run-metadata.json");
        var metadata = new RunMetadata(
            TargetName: "AtomicTarget",
            StartedAt: DateTimeOffset.UtcNow.ToString("o"),
            MachineInfo: new MachineInfo("Test CPU", 8, 32, "Test OS", ".NET Test"),
            Experiments: []);

        await adapter.SaveRunMetadataAsync(metadataPath, metadata, CancellationToken.None);

        _ = File.Exists(metadataPath).Should().BeTrue();
        _ = File.Exists(metadataPath + ".tmp").Should().BeFalse();

        RunMetadata persisted = await adapter.LoadRunMetadataAsync(metadataPath, CancellationToken.None)
            ?? throw new InvalidOperationException("Expected persisted run metadata.");

        _ = persisted.TargetName.Should().Be("AtomicTarget");
        _ = persisted.Experiments.Should().BeEmpty();
    }

    [Fact]
    public async Task AtomicFileWriter_WhenTempWriteFails_PreservesExistingMetadata()
    {
        string targetDir = CreateTargetDir("atomic-file-writer-target");
        string metadataPath = Path.Combine(targetDir, "hone-results", "run-metadata.json");
        string tempPath = metadataPath + ".tmp";

        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);

        byte[] originalBytes = Encoding.UTF8.GetBytes("""{"targetName":"original"}""");
        await File.WriteAllBytesAsync(metadataPath, originalBytes);

        bool moveCalled = false;
        byte[] updatedBytes = Encoding.UTF8.GetBytes("""{"targetName":"updated"}""");

        static async Task WriteTempFileAsync(string path, byte[] bytes, CancellationToken ct)
        {
            byte[] partialBytes = bytes[..Math.Min(8, bytes.Length)];
            await File.WriteAllBytesAsync(path, partialBytes, ct).ConfigureAwait(false);
            throw new IOException("Simulated temp write failure");
        }

        Func<Task> act = () => AtomicFileWriter.WriteBytesAsync(
            metadataPath,
            updatedBytes,
            WriteTempFileAsync,
            (_, _) => moveCalled = true,
            CancellationToken.None);

        _ = await act.Should().ThrowAsync<IOException>();
        _ = moveCalled.Should().BeFalse();

        byte[] persistedBytes = await File.ReadAllBytesAsync(metadataPath);
        _ = persistedBytes.Should().Equal(originalBytes);
        _ = File.Exists(tempPath).Should().BeTrue();
    }

    [Fact]
    public async Task BaselineCommand_RunsCleanupWhenPrepareFails()
    {
        string targetDir = CreateTargetDir("baseline-prepare-failure", builder =>
        {
            _ = builder.AddFile("MyApp.sln", string.Empty);
            _ = builder.AddFile("MyApp\\app.csproj", "<Project />");
            _ = builder.AddFile(".hone\\scenarios\\baseline.js", "export default function () {}");
            _ = builder.AddFile(".hone\\config.yaml", """
Name: "CleanupTarget"
BaseBranch: "main"
Api:
  SolutionPath: "MyApp.sln"
  ProjectPath: "MyApp"
  BaseUrl: "http://localhost:5000"
  ResultsPath: "hone-results"
  MetadataPath: "hone-results/metadata"
ScaleTest:
  ScenarioPath: ".hone/scenarios/baseline.js"
Hooks:
  Prepare:
    Type: Command
    Value: "exit 7"
  Build:
    Type: Skip
  Test:
    Type: Skip
  Start:
    Type: Skip
  Stop:
    Type: Skip
  Ready:
    Type: Skip
  Warmup:
    Type: Skip
  Active:
    Type: Skip
  Cooldown:
    Type: Skip
  Cleanup:
    Type: Command
    Value: "echo cleaned> cleanup-ran.txt"
""");
        });

        int exitCode = await Program.Main(["baseline", "--target", targetDir, "--force"]);

        _ = exitCode.Should().Be(1);
        _ = File.Exists(Path.Combine(targetDir, "cleanup-ran.txt")).Should().BeTrue();
    }

    private static HoneConfig CreateConfig(string baseUrl, string resultsPath, bool warmupEnabled) =>
        new(
            Api: new ApiConfig(
                SolutionPath: "MyApp.sln",
                ProjectPath: "MyApp",
                TestProjectPath: "MyApp.Tests",
                BaseUrl: baseUrl,
                ResultsPath: resultsPath,
                MetadataPath: Path.Combine(resultsPath, "metadata")),
            ScaleTest: new ScaleTestConfig(
                ScenarioPath: "baseline.js",
                ScenarioRegistryPath: null,
                WarmupEnabled: warmupEnabled,
                WarmupScenarioPath: warmupEnabled ? "warmup.js" : null,
                MeasuredRuns: 1,
                CooldownSeconds: 0));

    private static MetricSet CreateMetrics(int experiment, int run, string? summaryPath) =>
        new(
            Timestamp: DateTimeOffset.UtcNow.ToString("o"),
            Experiment: experiment,
            Run: run,
            HttpReqDuration: new HttpReqDurationMetrics(
                Avg: 100,
                P50: 90,
                P90: 115,
                P95: 125,
                P99: 140,
                Max: 180),
            HttpReqs: new HttpReqCountMetrics(Count: 5000, Rate: 250),
            HttpReqFailed: new HttpReqFailedMetrics(Count: 0, Rate: 0),
            SummaryPath: summaryPath);

    private sealed class CapturingHttpMessageHandler(List<Uri> capturedRequests) : HttpMessageHandler
    {
        private readonly List<Uri> _capturedRequests = capturedRequests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null)
            {
                _capturedRequests.Add(request.RequestUri);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class TimeoutHttpMessageHandler(List<Uri> capturedRequests) : HttpMessageHandler
    {
        private readonly List<Uri> _capturedRequests = capturedRequests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null)
            {
                _capturedRequests.Add(request.RequestUri);
            }

            throw new TaskCanceledException("Simulated timeout");
        }
    }
}
