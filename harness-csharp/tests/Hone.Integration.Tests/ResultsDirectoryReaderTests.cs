using FluentAssertions;
using Hone.Cli;
using Xunit;

namespace Hone.Integration.Tests;

/// <summary>
/// Tests for <see cref="ResultsDirectoryReader"/> — reads .hone/results directory structure.
/// </summary>
public sealed class ResultsDirectoryReaderTests : IDisposable
{
    private readonly string _tempDir;

    public ResultsDirectoryReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hone-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_WithBaseline_ReturnsBaselineMetrics()
    {
        // Arrange
        string baselineDir = Path.Combine(_tempDir, "baseline");
        Directory.CreateDirectory(baselineDir);
        await File.WriteAllTextAsync(
            Path.Combine(baselineDir, "k6-summary.json"),
            CreateK6SummaryJson(p95: 42.5, avg: 20.0, rps: 1000));

        // Act
        ResultsSnapshot snapshot = await ResultsDirectoryReader.LoadAsync(_tempDir);

        // Assert
        _ = snapshot.Baseline.Should().NotBeNull();
        _ = snapshot.Baseline.HttpReqDuration.P95.Should().Be(42.5);
        _ = snapshot.Baseline.HttpReqDuration.Avg.Should().Be(20.0);
        _ = snapshot.Baseline.HttpReqs.Rate.Should().Be(1000);
    }

    [Fact]
    public async Task LoadAsync_WithExperiments_ReturnsExperimentsSorted()
    {
        // Arrange
        await CreateBaselineFileAsync();
        await CreateExperimentFileAsync(2, p95: 38.0);
        await CreateExperimentFileAsync(1, p95: 40.0);
        await CreateExperimentFileAsync(3, p95: 35.0);

        // Act
        ResultsSnapshot snapshot = await ResultsDirectoryReader.LoadAsync(_tempDir);

        // Assert
        _ = snapshot.Experiments.Should().HaveCount(3);
        _ = snapshot.Experiments[0].Experiment.Should().Be(1);
        _ = snapshot.Experiments[1].Experiment.Should().Be(2);
        _ = snapshot.Experiments[2].Experiment.Should().Be(3);
        _ = snapshot.Experiments[0].Metrics.HttpReqDuration.P95.Should().Be(40.0);
        _ = snapshot.Experiments[2].Metrics.HttpReqDuration.P95.Should().Be(35.0);
    }

    [Fact]
    public async Task LoadAsync_WithRunMetadata_LoadsMetadata()
    {
        // Arrange
        await CreateBaselineFileAsync();
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "run-metadata.json"),
            """{"TargetName":"sample-api","StartedAt":"2026-01-01T00:00:00Z","MachineInfo":null,"Experiments":[]}""");

        // Act
        ResultsSnapshot snapshot = await ResultsDirectoryReader.LoadAsync(_tempDir);

        // Assert
        _ = snapshot.Metadata.Should().NotBeNull();
        _ = snapshot.Metadata!.TargetName.Should().Be("sample-api");
    }

    [Fact]
    public async Task LoadAsync_WithCounterData_ReturnsCounterMetrics()
    {
        // Arrange
        await CreateBaselineFileAsync();
        string expDir = Path.Combine(_tempDir, "experiment-1", "diagnostics", "dotnet-counters");
        Directory.CreateDirectory(expDir);
        await CreateExperimentFileAsync(1, p95: 40.0);
        await File.WriteAllTextAsync(
            Path.Combine(expDir, "dotnet-counters.json"),
            """{"TotalSamples":10,"Runtime":{"CpuUsage":{"Avg":25.5,"Min":10,"Max":40,"Last":30,"Samples":10},"WorkingSetMB":{"Avg":512.0,"Min":400,"Max":600,"Last":550,"Samples":10}}}""");

        // Act
        ResultsSnapshot snapshot = await ResultsDirectoryReader.LoadAsync(_tempDir);

        // Assert
        _ = snapshot.Experiments.Should().HaveCount(1);
        _ = snapshot.Experiments[0].Counters.Should().NotBeNull();
        _ = snapshot.Experiments[0].Counters!.CpuAvgPercent.Should().Be(25.5);
        _ = snapshot.Experiments[0].Counters!.MemoryMB.Should().Be(512.0);
    }

    [Fact]
    public async Task LoadAsync_WithScenarios_LoadsScenarioData()
    {
        // Arrange
        await CreateBaselineFileAsync();
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "baseline-health.json"),
            CreateK6SummaryJson(p95: 5.0, avg: 2.0, rps: 5000));

        await CreateExperimentFileAsync(1, p95: 40.0);
        string expDir = Path.Combine(_tempDir, "experiment-1");
        await File.WriteAllTextAsync(
            Path.Combine(expDir, "k6-summary-health.json"),
            CreateK6SummaryJson(p95: 4.0, avg: 1.5, rps: 6000));

        // Act
        ResultsSnapshot snapshot = await ResultsDirectoryReader.LoadAsync(_tempDir);

        // Assert
        _ = snapshot.Scenarios.Should().HaveCount(1);
        _ = snapshot.Scenarios[0].ScenarioName.Should().Be("health");
        _ = snapshot.Scenarios[0].Baseline.Metrics.HttpReqDuration.P95.Should().Be(5.0);
        _ = snapshot.Scenarios[0].Experiments.Should().HaveCount(1);
        _ = snapshot.Scenarios[0].Experiments[0].Metrics.HttpReqDuration.P95.Should().Be(4.0);
    }

    [Fact]
    public async Task LoadAsync_NoBaseline_ThrowsFileNotFoundException()
    {
        // Act & Assert
        Func<Task> act = () => ResultsDirectoryReader.LoadAsync(_tempDir);
        _ = await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task LoadAsync_MissingDirectory_ThrowsDirectoryNotFoundException()
    {
        // Act & Assert
        Func<Task> act = () => ResultsDirectoryReader.LoadAsync(Path.Combine(_tempDir, "nonexistent"));
        _ = await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task LoadAsync_LegacyBaselineJson_FallsBackCorrectly()
    {
        // Arrange — use baseline.json (legacy) instead of baseline/k6-summary.json
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "baseline.json"),
            CreateK6SummaryJson(p95: 50.0, avg: 25.0, rps: 800));

        // Act
        ResultsSnapshot snapshot = await ResultsDirectoryReader.LoadAsync(_tempDir);

        // Assert
        _ = snapshot.Baseline.HttpReqDuration.P95.Should().Be(50.0);
    }

    [Fact]
    public async Task LoadAsync_NoExperiments_ReturnsEmptyList()
    {
        // Arrange
        await CreateBaselineFileAsync();

        // Act
        ResultsSnapshot snapshot = await ResultsDirectoryReader.LoadAsync(_tempDir);

        // Assert
        _ = snapshot.Experiments.Should().BeEmpty();
        _ = snapshot.Scenarios.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_MissingMetadata_ReturnsNull()
    {
        // Arrange
        await CreateBaselineFileAsync();

        // Act
        ResultsSnapshot snapshot = await ResultsDirectoryReader.LoadAsync(_tempDir);

        // Assert
        _ = snapshot.Metadata.Should().BeNull();
        _ = snapshot.BaselineCounters.Should().BeNull();
    }

    // ── Helpers ──

    private async Task CreateBaselineFileAsync()
    {
        string baselineDir = Path.Combine(_tempDir, "baseline");
        Directory.CreateDirectory(baselineDir);
        await File.WriteAllTextAsync(
            Path.Combine(baselineDir, "k6-summary.json"),
            CreateK6SummaryJson(p95: 42.5, avg: 20.0, rps: 1000)).ConfigureAwait(false);
    }

    private async Task CreateExperimentFileAsync(int experiment, double p95)
    {
        string expDir = Path.Combine(_tempDir, $"experiment-{experiment}");
        Directory.CreateDirectory(expDir);
        await File.WriteAllTextAsync(
            Path.Combine(expDir, "k6-summary.json"),
            CreateK6SummaryJson(p95: p95, avg: p95 / 2, rps: 1000)).ConfigureAwait(false);
    }

    private static string CreateK6SummaryJson(double p95, double avg, double rps) =>
        $$"""
        {
          "metrics": {
            "http_req_duration": {
              "avg": {{avg}},
              "med": {{avg * 0.9}},
              "p(90)": {{p95 * 0.95}},
              "p(95)": {{p95}},
              "p(99)": {{p95 * 1.1}},
              "max": {{p95 * 1.5}}
            },
            "http_reqs": {
              "count": 10000,
              "rate": {{rps}}
            },
            "http_req_failed": {
              "value": 0.0,
              "passes": 0
            }
          }
        }
        """;
}
