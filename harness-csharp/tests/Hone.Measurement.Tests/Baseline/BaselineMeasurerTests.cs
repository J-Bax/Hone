using FluentAssertions;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Measurement.Baseline;
using Hone.TestInfrastructure;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Measurement.Tests.Baseline;

public sealed class BaselineMeasurerTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private static readonly Uri TestBaseUrl = new("http://localhost:5000");

    private readonly ILoadTestRunner _runner = Substitute.For<ILoadTestRunner>();
    private readonly IRuntimeMetricsCollector _collector = Substitute.For<IRuntimeMetricsCollector>();

    [Fact]
    public async Task MeasureAsync_RunsScaleTests_ReturnsMetrics()
    {
        // Arrange
        var config = new ScaleTestConfig(
            WarmupEnabled: false,
            MeasuredRuns: 3,
            CooldownSeconds: 0);

        _ = _runner.RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                LoadTestOptions opts = ci.Arg<LoadTestOptions>();
                return MakeLoadTestResult(p95: 200, run: opts.Run);
            });

        // Act
        BaselineResult result = await BaselineMeasurer.MeasureAsync(
            config, countersConfig: null, _runner, collector: null,
            TestBaseUrl, TempDir, processId: 1234);

        // Assert
        _ = result.Success.Should().BeTrue();
        _ = result.Metrics.Should().NotBeNull();
        _ = result.ScaleTestDetail.Should().NotBeNull();
        _ = result.ScaleTestDetail!.RunCount.Should().Be(3);
        _ = result.CounterMetrics.Should().BeNull();

        // Verify runner was called for each measured run
        _ = await _runner.Received(3).RunAsync(
            Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MeasureAsync_WithCounterCollection_CollectsAndReturns()
    {
        // Arrange
        var config = new ScaleTestConfig(
            WarmupEnabled: false,
            MeasuredRuns: 1,
            CooldownSeconds: 0);

        var countersConfig = new DotnetCountersConfig(Enabled: true);
        var handle = new MetricsCollectionHandle(new object());
        var expectedCounters = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["cpu-usage"] = 42.5,
            ["working-set"] = 1024.0,
        };

        _ = _collector.StartAsync(Arg.Any<int>(), Arg.Any<RuntimeMetricsOptions>(), Arg.Any<CancellationToken>())
            .Returns(handle);

        _ = _collector.StopAndParseAsync(handle, Arg.Any<CancellationToken>())
            .Returns(new RuntimeMetricsResult(Success: true, Counters: expectedCounters));

        _ = _runner.RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>())
            .Returns(MakeLoadTestResult(p95: 100, run: 1));

        // Act
        BaselineResult result = await BaselineMeasurer.MeasureAsync(
            config, countersConfig, _runner, _collector,
            TestBaseUrl, TempDir, processId: 5678);

        // Assert
        _ = result.Success.Should().BeTrue();
        _ = result.CounterMetrics.Should().NotBeNull();
        _ = result.CounterMetrics.Should().ContainKey("cpu-usage").WhoseValue.Should().Be(42.5);
        _ = result.CounterMetrics.Should().ContainKey("working-set").WhoseValue.Should().Be(1024.0);

        // Verify collector was started and stopped
        _ = await _collector.Received(1).StartAsync(
            5678, Arg.Any<RuntimeMetricsOptions>(), Arg.Any<CancellationToken>());
        _ = await _collector.Received(1).StopAndParseAsync(
            handle, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MeasureAsync_WithoutCollector_SkipsCounters()
    {
        // Arrange
        var config = new ScaleTestConfig(
            WarmupEnabled: false,
            MeasuredRuns: 1,
            CooldownSeconds: 0);

        var countersConfig = new DotnetCountersConfig(Enabled: true);

        _ = _runner.RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>())
            .Returns(MakeLoadTestResult(p95: 100, run: 1));

        // Act — collector is null even though config says enabled
        BaselineResult result = await BaselineMeasurer.MeasureAsync(
            config, countersConfig, _runner, collector: null,
            TestBaseUrl, TempDir, processId: 1234);

        // Assert
        _ = result.Success.Should().BeTrue();
        _ = result.CounterMetrics.Should().BeNull();
    }

    [Fact]
    public async Task MeasureAsync_NoMetricsFromScaleTest_ReturnsFailed()
    {
        // Arrange: runner returns null metrics for all runs
        var config = new ScaleTestConfig(
            WarmupEnabled: false,
            MeasuredRuns: 3,
            CooldownSeconds: 0);

        _ = _runner.RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LoadTestResult(Success: false, Metrics: null, SummaryPath: null, Output: null));

        // Act
        BaselineResult result = await BaselineMeasurer.MeasureAsync(
            config, countersConfig: null, _runner, collector: null,
            TestBaseUrl, TempDir, processId: 1234);

        // Assert
        _ = result.Success.Should().BeFalse();
        _ = result.Metrics.Should().BeNull();
        _ = result.ScaleTestDetail.Should().NotBeNull();
        _ = result.ScaleTestDetail!.RunCount.Should().Be(0);
    }

    [Fact]
    public async Task MeasureAsync_CounterCollectionFails_StillSucceeds()
    {
        // Arrange: counters fail but load test metrics succeed
        var config = new ScaleTestConfig(
            WarmupEnabled: false,
            MeasuredRuns: 1,
            CooldownSeconds: 0);

        var countersConfig = new DotnetCountersConfig(Enabled: true);
        var handle = new MetricsCollectionHandle(new object());

        _ = _collector.StartAsync(Arg.Any<int>(), Arg.Any<RuntimeMetricsOptions>(), Arg.Any<CancellationToken>())
            .Returns(handle);

        _ = _collector.StopAndParseAsync(handle, Arg.Any<CancellationToken>())
            .Returns(new RuntimeMetricsResult(Success: false, Counters: null));

        _ = _runner.RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>())
            .Returns(MakeLoadTestResult(p95: 150, run: 1));

        // Act
        BaselineResult result = await BaselineMeasurer.MeasureAsync(
            config, countersConfig, _runner, _collector,
            TestBaseUrl, TempDir, processId: 9999);

        // Assert — success because metrics exist (counters are optional)
        _ = result.Success.Should().BeTrue();
        _ = result.Metrics.Should().NotBeNull();
        _ = result.CounterMetrics.Should().BeNull();
    }

    [Fact]
    public async Task MeasureAsync_UsesExperimentZero()
    {
        // Arrange: verify experiment=0 is passed for baselines
        var config = new ScaleTestConfig(
            WarmupEnabled: false,
            MeasuredRuns: 1,
            CooldownSeconds: 0);

        int? capturedExperiment = null;

        _ = _runner.RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                LoadTestOptions opts = ci.Arg<LoadTestOptions>();
                capturedExperiment = opts.Experiment;
                return MakeLoadTestResult(p95: 100, run: opts.Run);
            });

        // Act
        _ = await BaselineMeasurer.MeasureAsync(
            config, countersConfig: null, _runner, collector: null,
            TestBaseUrl, TempDir, processId: 1234);

        // Assert
        _ = capturedExperiment.Should().Be(0);
    }

    [Fact]
    public async Task MeasureAsync_CountersDisabled_SkipsCollection()
    {
        // Arrange: countersConfig exists but Enabled = false
        var config = new ScaleTestConfig(
            WarmupEnabled: false,
            MeasuredRuns: 1,
            CooldownSeconds: 0);

        var countersConfig = new DotnetCountersConfig(Enabled: false);

        _ = _runner.RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>())
            .Returns(MakeLoadTestResult(p95: 100, run: 1));

        // Act
        BaselineResult result = await BaselineMeasurer.MeasureAsync(
            config, countersConfig, _runner, _collector,
            TestBaseUrl, TempDir, processId: 1234);

        // Assert
        _ = result.Success.Should().BeTrue();
        _ = result.CounterMetrics.Should().BeNull();

        // Collector should never have been called
        _ = await _collector.DidNotReceive().StartAsync(
            Arg.Any<int>(), Arg.Any<RuntimeMetricsOptions>(), Arg.Any<CancellationToken>());
    }

    #region Helpers

    private static LoadTestResult MakeLoadTestResult(double p95, int run)
    {
        var metrics = new MetricSet(
            Timestamp: "2024-01-01T00:00:00Z",
            Experiment: 0,
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
