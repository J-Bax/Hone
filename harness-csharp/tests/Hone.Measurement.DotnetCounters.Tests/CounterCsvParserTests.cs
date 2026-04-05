using FluentAssertions;
using Hone.Measurement.Comparison;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Measurement.DotnetCounters.Tests;

public sealed class CounterCsvParserTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public async Task Parse_ExtractsAllProviders()
    {
        // Arrange
        string csv = await LoadFixtureAsync("dotnet-counters-sample.csv");

        // Act
        CounterParseResult result = CounterCsvParser.Parse(csv);

        // Assert
        _ = result.StructuredMetrics.Should().NotBeNull();
        RuntimeCounterMetrics m = result.StructuredMetrics!;

        _ = m.CpuUsage.Samples.Should().Be(3);
        _ = m.WorkingSetMB.Samples.Should().Be(3);
        _ = m.GcHeapSizeMB.Samples.Should().Be(2);
        _ = m.Gen0Collections.Samples.Should().Be(2);
        _ = m.Gen1Collections.Samples.Should().Be(2);
        _ = m.Gen2Collections.Samples.Should().Be(2);
        _ = m.GcPauseRatio.Samples.Should().Be(2);
        _ = m.AllocRateMB.Samples.Should().Be(2);
        _ = m.ExceptionCount.Samples.Should().Be(2);
        _ = m.ThreadPoolThreads.Samples.Should().Be(2);
        _ = m.ThreadPoolQueueLength.Samples.Should().Be(2);
    }

    [Fact]
    public void Parse_EmptyContent_ReturnsEmptyResult()
    {
        // Act
        CounterParseResult result = CounterCsvParser.Parse(string.Empty);

        // Assert
        _ = result.Counters.Should().BeEmpty();
        _ = result.StructuredMetrics.Should().BeNull();
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsEmptyResult()
    {
        // Act
        CounterParseResult result = CounterCsvParser.Parse("   \n  \n  ");

        // Assert
        _ = result.Counters.Should().BeEmpty();
        _ = result.StructuredMetrics.Should().BeNull();
    }

    [Fact]
    public void Parse_HeaderOnly_ReturnsEmptyResult()
    {
        // Arrange
        const string Csv = "Timestamp,Provider,Counter Name,Counter Type,Mean/Increment\n";

        // Act
        CounterParseResult result = CounterCsvParser.Parse(Csv);

        // Assert
        _ = result.Counters.Should().BeEmpty();
        _ = result.StructuredMetrics.Should().BeNull();
    }

    [Fact]
    public async Task Parse_ComputesStatisticsCorrectly()
    {
        // Arrange — three known CPU Usage values: 45.2, 47.1, 43.0
        string csv = await LoadFixtureAsync("dotnet-counters-sample.csv");

        // Act
        CounterParseResult result = CounterCsvParser.Parse(csv);

        // Assert — CpuUsage statistics
        RuntimeCounterMetrics m = result.StructuredMetrics!;

        _ = m.CpuUsage.Min.Should().Be(43.0);
        _ = m.CpuUsage.Max.Should().Be(47.1);
        _ = m.CpuUsage.Last.Should().Be(43.0);
        _ = m.CpuUsage.Samples.Should().Be(3);
        _ = m.CpuUsage.Avg.Should().BeApproximately(45.1, 0.01);

        // Verify flattened dictionary keys
        _ = result.Counters["CpuUsage.Avg"].Should().BeApproximately(45.1, 0.01);
        _ = result.Counters["CpuUsage.Min"].Should().Be(43.0);
        _ = result.Counters["CpuUsage.Max"].Should().Be(47.1);
        _ = result.Counters["CpuUsage.Last"].Should().Be(43.0);
        _ = result.Counters["CpuUsage.Samples"].Should().Be(3);

        // Verify Working Set: 150.5, 152.3, 151.0
        _ = m.WorkingSetMB.Min.Should().Be(150.5);
        _ = m.WorkingSetMB.Max.Should().Be(152.3);
        _ = m.WorkingSetMB.Last.Should().Be(151.0);
        _ = m.WorkingSetMB.Avg.Should().BeApproximately(151.267, 0.01);

        // Verify Gen2 collections: 0, 1
        _ = m.Gen2Collections.Min.Should().Be(0);
        _ = m.Gen2Collections.Max.Should().Be(1);
        _ = m.Gen2Collections.Last.Should().Be(1);
        _ = m.Gen2Collections.Avg.Should().Be(0.5);

        // Verify ThreadPool Queue: 0, 3
        _ = m.ThreadPoolQueueLength.Min.Should().Be(0);
        _ = m.ThreadPoolQueueLength.Max.Should().Be(3);
        _ = m.ThreadPoolQueueLength.Avg.Should().Be(1.5);
    }

    [Fact]
    public void Parse_MissingCounters_UsesZeroStatistic()
    {
        // Arrange — CSV with only CPU Usage rows; other counters should be zero.
        const string Csv = """
            Timestamp,Provider,Counter Name,Counter Type,Mean/Increment
            2024-01-01 00:00:00,System.Runtime,CPU Usage,Mean,10.0
            """;

        // Act
        CounterParseResult result = CounterCsvParser.Parse(Csv);

        // Assert
        _ = result.StructuredMetrics.Should().NotBeNull();
        _ = result.StructuredMetrics!.CpuUsage.Avg.Should().Be(10.0);
        _ = result.StructuredMetrics.WorkingSetMB.Should().Be(new CounterStatistic(0, 0, 0, 0, 0));
        _ = result.StructuredMetrics.GcHeapSizeMB.Should().Be(new CounterStatistic(0, 0, 0, 0, 0));
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