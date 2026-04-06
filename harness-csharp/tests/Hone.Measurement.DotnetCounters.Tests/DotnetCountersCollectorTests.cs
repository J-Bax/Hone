using FluentAssertions;
using Hone.Core.Contracts;
using Hone.TestInfrastructure;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Measurement.DotnetCounters.Tests;

public sealed class DotnetCountersCollectorTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();

    [Fact]
    public async Task StartAsync_ReturnsHandleWithOutputPath()
    {
        // Arrange
        var collector = new DotnetCountersCollector(_processRunner);
        var options = new RuntimeMetricsOptions(
            Providers: ["System.Runtime"],
            RefreshIntervalSeconds: 1);

        // Act
        MetricsCollectionHandle handle = await collector.StartAsync(1234, options);

        // Assert
        _ = handle.Should().NotBeNull();
        _ = handle.Handle.Should().BeOfType<DotnetCountersHandle>();

        var dcHandle = (DotnetCountersHandle)handle.Handle;
        _ = dcHandle.ProcessId.Should().Be(1234);
        _ = dcHandle.OutputPath.Should().EndWith(".csv");
        _ = dcHandle.OutputPath.Should().Contain("counters_1234_");
    }

    [Fact]
    public async Task StartAsync_InvalidProcessId_Throws()
    {
        // Arrange
        var collector = new DotnetCountersCollector(_processRunner);
        var options = new RuntimeMetricsOptions(
            Providers: ["System.Runtime"],
            RefreshIntervalSeconds: 1);

        // Act
        Func<Task> act = () => collector.StartAsync(0, options);

        // Assert
        _ = await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task StopAndParseAsync_ValidCsv_ReturnsMetrics()
    {
        // Arrange — write fixture CSV to the handle path
        var collector = new DotnetCountersCollector(_processRunner);
        string csvPath = Path.Combine(TempDir, "counters.csv");
        string csv = await LoadFixtureAsync("dotnet-counters-sample.csv");
        await File.WriteAllTextAsync(csvPath, csv);

        var handle = new MetricsCollectionHandle(new DotnetCountersHandle(csvPath, 1234));

        // Act
        RuntimeMetricsResult result = await collector.StopAndParseAsync(handle);

        // Assert
        _ = result.Success.Should().BeTrue();
        _ = result.Counters.Should().NotBeNull();
        _ = result.Counters!.Should().ContainKey("CpuUsage.Avg");
        _ = result.Counters["CpuUsage.Avg"].Should().BeApproximately(45.1, 0.01);
    }

    [Fact]
    public async Task StopAndParseAsync_MissingFile_ReturnsFailure()
    {
        // Arrange
        var collector = new DotnetCountersCollector(_processRunner);
        string csvPath = Path.Combine(TempDir, "nonexistent.csv");
        var handle = new MetricsCollectionHandle(new DotnetCountersHandle(csvPath, 1234));

        // Act
        RuntimeMetricsResult result = await collector.StopAndParseAsync(handle);

        // Assert
        _ = result.Success.Should().BeFalse();
        _ = result.Counters.Should().BeNull();
    }

    [Fact]
    public async Task StopAndParseAsync_InvalidHandle_ReturnsFailure()
    {
        // Arrange
        var collector = new DotnetCountersCollector(_processRunner);
        var handle = new MetricsCollectionHandle(new object());

        // Act
        RuntimeMetricsResult result = await collector.StopAndParseAsync(handle);

        // Assert
        _ = result.Success.Should().BeFalse();
        _ = result.Counters.Should().BeNull();
    }

    private static async Task<string> LoadFixtureAsync(string relativePath)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            string candidate = Path.Combine(directory, "test-fixtures", relativePath);
            if (File.Exists(candidate))
            {
                return await File.ReadAllTextAsync(candidate).ConfigureAwait(false);
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new FileNotFoundException($"Test fixture not found: {relativePath}");
    }
}

