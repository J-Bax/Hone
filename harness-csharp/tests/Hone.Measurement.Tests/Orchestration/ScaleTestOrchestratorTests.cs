using FluentAssertions;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Measurement.Orchestration;
using Hone.TestInfrastructure;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Measurement.Tests.Orchestration;

public sealed class ScaleTestOrchestratorTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private static readonly Uri TestBaseUrl = new("http://localhost:5000");

    private readonly ILoadTestRunner _runner = Substitute.For<ILoadTestRunner>();

    [Fact]
    public async Task RunAsync_MultipleRuns_SelectsMedian()
    {
        // Arrange: 5 runs with known p95 values: 100, 300, 200, 500, 400
        // After sorting by p95: [100, 200, 300, 400, 500] → index 2 → p95=300
        var config = new ScaleTestConfig(
            WarmupEnabled: false,
            MeasuredRuns: 5,
            CooldownSeconds: 0);

        double[] p95Values = [100, 300, 200, 500, 400];
        int callIndex = 0;

        _ = _runner.RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                int idx = callIndex++;
                return MakeLoadTestResult(p95Values[idx], run: idx + 1);
            });

        // Act
        ScaleTestResult result = await ScaleTestOrchestrator.RunAsync(
            config, _runner, TestBaseUrl, TempDir, experiment: 1);

        // Assert
        _ = result.Success.Should().BeTrue();
        _ = result.RunCount.Should().Be(5);
        _ = result.Metrics.Should().NotBeNull();
        _ = result.Metrics!.HttpReqDuration.P95.Should().Be(300);
        _ = result.RunMetrics.Should().HaveCount(5);
    }

    [Fact]
    public async Task RunAsync_WarmupEnabled_RunsWarmupFirst()
    {
        // Arrange
        const string WarmupPath = "scenarios/warmup.js";
        const string MainPath = "scenarios/baseline.js";

        var config = new ScaleTestConfig(
            ScenarioPath: MainPath,
            WarmupEnabled: true,
            WarmupScenarioPath: WarmupPath,
            MeasuredRuns: 2,
            CooldownSeconds: 0);

        List<(string ScenarioPath, int Run)> callLog = [];

        _ = _runner.RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                LoadTestOptions opts = ci.Arg<LoadTestOptions>();
                callLog.Add((opts.ScenarioPath, opts.Run));
                return MakeLoadTestResult(p95: 100, run: opts.Run);
            });

        // Act
        _ = await ScaleTestOrchestrator.RunAsync(
            config, _runner, TestBaseUrl, TempDir, experiment: 1);

        // Assert: warmup called first with run=0
        _ = callLog.Should().HaveCount(3); // 1 warmup + 2 measured
        _ = callLog[0].ScenarioPath.Should().Be(WarmupPath);
        _ = callLog[0].Run.Should().Be(0);
        _ = callLog[1].ScenarioPath.Should().Be(MainPath);
        _ = callLog[1].Run.Should().Be(1);
        _ = callLog[2].ScenarioPath.Should().Be(MainPath);
        _ = callLog[2].Run.Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_CooldownBetweenRuns()
    {
        // Arrange: verify runner is called the right number of times in order
        var config = new ScaleTestConfig(
            WarmupEnabled: false,
            MeasuredRuns: 3,
            CooldownSeconds: 0); // 0 to keep test fast

        List<int> runNumbers = [];

        _ = _runner.RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                LoadTestOptions opts = ci.Arg<LoadTestOptions>();
                runNumbers.Add(opts.Run);
                return MakeLoadTestResult(p95: 100, run: opts.Run);
            });

        // Act
        ScaleTestResult result = await ScaleTestOrchestrator.RunAsync(
            config, _runner, TestBaseUrl, TempDir, experiment: 1);

        // Assert: all 3 runs executed in order
        _ = result.RunCount.Should().Be(3);
        _ = runNumbers.Should().BeEquivalentTo([1, 2, 3]);
        _ = runNumbers.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task RunAsync_SingleRun_ReturnsDirectly()
    {
        // Arrange
        var config = new ScaleTestConfig(
            WarmupEnabled: false,
            MeasuredRuns: 1,
            CooldownSeconds: 0);

        _ = _runner.RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>())
            .Returns(MakeLoadTestResult(p95: 42, run: 1));

        // Act
        ScaleTestResult result = await ScaleTestOrchestrator.RunAsync(
            config, _runner, TestBaseUrl, TempDir, experiment: 1);

        // Assert
        _ = result.Success.Should().BeTrue();
        _ = result.RunCount.Should().Be(1);
        _ = result.Metrics.Should().NotBeNull();
        _ = result.Metrics!.HttpReqDuration.P95.Should().Be(42);
    }

    [Fact]
    public async Task RunAsync_AllRunsFailed_ReturnsFailure()
    {
        // Arrange: all runs return null metrics
        var config = new ScaleTestConfig(
            WarmupEnabled: false,
            MeasuredRuns: 3,
            CooldownSeconds: 0);

        _ = _runner.RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LoadTestResult(Success: false, Metrics: null, SummaryPath: null, Output: null));

        // Act
        ScaleTestResult result = await ScaleTestOrchestrator.RunAsync(
            config, _runner, TestBaseUrl, TempDir, experiment: 1);

        // Assert
        _ = result.Success.Should().BeFalse();
        _ = result.Metrics.Should().BeNull();
        _ = result.RunCount.Should().Be(0);
        _ = result.RunMetrics.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_WarmupDisabled_SkipsWarmup()
    {
        // Arrange
        var config = new ScaleTestConfig(
            ScenarioPath: "scenarios/baseline.js",
            WarmupEnabled: false,
            WarmupScenarioPath: "scenarios/warmup.js",
            MeasuredRuns: 1,
            CooldownSeconds: 0);

        List<string> scenarioPaths = [];

        _ = _runner.RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                LoadTestOptions opts = ci.Arg<LoadTestOptions>();
                scenarioPaths.Add(opts.ScenarioPath);
                return MakeLoadTestResult(p95: 100, run: opts.Run);
            });

        // Act
        _ = await ScaleTestOrchestrator.RunAsync(
            config, _runner, TestBaseUrl, TempDir, experiment: 1);

        // Assert: only the measured run, no warmup
        _ = scenarioPaths.Should().HaveCount(1);
        _ = scenarioPaths[0].Should().Be("scenarios/baseline.js");
    }

    [Fact]
    public async Task RunAsync_PassesWorkingDirectoryToRunner()
    {
        // Arrange
        string workingDirectory = Path.Combine(TempDir, "target");
        var config = new ScaleTestConfig(
            WarmupEnabled: true,
            WarmupScenarioPath: "scenarios/warmup.js",
            ScenarioPath: "scenarios/baseline.js",
            MeasuredRuns: 2,
            CooldownSeconds: 0);

        List<string?> workingDirectories = [];

        _ = _runner.RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                LoadTestOptions opts = ci.Arg<LoadTestOptions>();
                workingDirectories.Add(opts.WorkingDirectory);
                return MakeLoadTestResult(p95: 100, run: opts.Run);
            });

        // Act
        _ = await ScaleTestOrchestrator.RunAsync(
            config,
            _runner,
            TestBaseUrl,
            TempDir,
            experiment: 1,
            ct: default,
            workingDirectory: workingDirectory);

        // Assert
        _ = workingDirectories.Should().HaveCount(3);
        _ = workingDirectories.Should().OnlyContain(value => value == workingDirectory);
    }

    #region Helpers

    private static LoadTestResult MakeLoadTestResult(double p95, int run)
    {
        var metrics = new MetricSet(
            Timestamp: "2024-01-01T00:00:00Z",
            Experiment: 1,
            Run: run,
            HttpReqDuration: new HttpReqDurationMetrics(
                Avg: p95 * 0.8,
                P50: p95 * 0.7,
                P90: p95 * 0.95,
                P95: p95,
                P99: p95 * 1.1,
                Max: p95 * 1.5),
            HttpReqs: new HttpReqCountMetrics(Count: 1000, Rate: 100),
            HttpReqFailed: new HttpReqFailedMetrics(Count: 0, Rate: 0),
            SummaryPath: null);

        return new LoadTestResult(
            Success: true,
            Metrics: metrics,
            SummaryPath: null,
            Output: null);
    }

    #endregion
}
