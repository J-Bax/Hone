using System.Text.Json;

using FluentAssertions;

using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Diagnostics.Collectors;
using Hone.TestInfrastructure;

using NSubstitute;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Diagnostics.Tests.Collectors;

public sealed class DotnetCountersCollectorPluginTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
    };

    [Fact]
    public async Task Start_BuildsCorrectArguments()
    {
        IProcessRunner runner = Substitute.For<IProcessRunner>();

        // Return immediately to capture args
        _ = runner.RunAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(),
                Arg.Any<string?>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(Success: true, Output: "", ExitCode: 0, TimedOut: false));

        var collector = new DotnetCountersCollectorPlugin(runner);
        var settings = new CollectorSettings(new Dictionary<string, object?>(StringComparer.Ordinal));
        string outputDir = Path.Combine(TempDir, "output");

        _ = await collector.StartAsync(5678, outputDir, settings);

        NSubstitute.Core.ICall countersCall = runner.ReceivedCalls()
            .First(c => ((string)c.GetArguments()[0]!).Contains("dotnet-counters", StringComparison.OrdinalIgnoreCase));

        var args = ((IEnumerable<string>)countersCall.GetArguments()[1]!).ToList();

        _ = args.Should().Contain("collect");
        _ = args.Should().Contain("--process-id");
        _ = args.Should().Contain("5678");
        _ = args.Should().Contain("--format");
        _ = args.Should().Contain("csv");
    }

    [Fact]
    public async Task Stop_ReturnsFailureForInvalidHandle()
    {
        IProcessRunner runner = Substitute.For<IProcessRunner>();
        var collector = new DotnetCountersCollectorPlugin(runner);

        CollectorArtifacts result = await collector.StopAsync("invalid-handle");

        _ = result.Success.Should().BeFalse();
        _ = result.ArtifactPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task Export_BuildsSummaryFromJson()
    {
        IProcessRunner runner = Substitute.For<IProcessRunner>();
        var collector = new DotnetCountersCollectorPlugin(runner);

        // Create a pre-parsed JSON artifact
        string jsonPath = Path.Combine(TempDir, "dotnet-counters.json");
        var metrics = new
        {
            TotalSamples = 100,
            Runtime = new
            {
                CpuUsage = new { Avg = 45.2, Min = 10.0, Max = 80.0, Last = 42.0, Samples = 100 },
                GcHeapSizeMB = new { Avg = 128.5, Min = 64.0, Max = 256.78, Last = 200.0, Samples = 100 },
                Gen2Collections = new { Avg = 2.0, Min = 0.0, Max = 3.0, Last = 3.0, Samples = 100 },
                GcPauseRatio = new { Avg = 1.5, Min = 0.5, Max = 2.34, Last = 1.8, Samples = 100 },
                ThreadPoolThreads = new { Avg = 8.0, Min = 4.0, Max = 12.0, Last = 10.0, Samples = 100 },
                AllocRateMB = new { Avg = 123.45, Min = 50.0, Max = 200.0, Last = 150.0, Samples = 100 },
            },
        };

        string json = JsonSerializer.Serialize(metrics, IndentedJsonOptions);
        await File.WriteAllTextAsync(jsonPath, json);

        string outputDir = Path.Combine(TempDir, "export");
        var settings = new CollectorSettings(new Dictionary<string, object?>(StringComparer.Ordinal));

        CollectorExportResult result = await collector.ExportAsync(
            [jsonPath], outputDir, "dotnet", settings);

        _ = result.Success.Should().BeTrue();
        _ = result.Summary.Should().NotBeNull();
        _ = result.Summary.Should().Contain("CPU avg: 45.2%");
        _ = result.Summary.Should().Contain("GC heap max: 256.78 MB");
        _ = result.Summary.Should().Contain("Gen2 collections: 3");
        _ = result.Summary.Should().Contain("GC pause max: 2.34%");
        _ = result.Summary.Should().Contain("Thread pool max: 12");
        _ = result.Summary.Should().Contain("Alloc rate avg: 123.45 MB/s");
    }

    [Fact]
    public async Task Export_ParsesCsvWhenNoJson()
    {
        IProcessRunner runner = Substitute.For<IProcessRunner>();
        var collector = new DotnetCountersCollectorPlugin(runner);

        // Create a CSV artifact matching dotnet-counters format
        string csvPath = Path.Combine(TempDir, "dotnet-counters.csv");
        string csvContent = """
            "Timestamp","Provider","Counter Name","Counter Type","Mean/Increment"
            "2024-01-01 00:00:01","System.Runtime","CPU Usage","Mean","45.2"
            "2024-01-01 00:00:02","System.Runtime","CPU Usage","Mean","50.0"
            "2024-01-01 00:00:01","System.Runtime","GC Heap Size","Mean","128.5"
            "2024-01-01 00:00:01","System.Runtime","Gen 2 GC Count","Increment","3"
            "2024-01-01 00:00:01","System.Runtime","% time in GC","Mean","1.8"
            "2024-01-01 00:00:01","System.Runtime","ThreadPool Thread Count","Mean","8"
            "2024-01-01 00:00:01","System.Runtime","Allocation Rate","Mean","100.5"
            """;
        await File.WriteAllTextAsync(csvPath, csvContent);

        string outputDir = Path.Combine(TempDir, "export");
        var settings = new CollectorSettings(new Dictionary<string, object?>(StringComparer.Ordinal));

        CollectorExportResult result = await collector.ExportAsync(
            [csvPath], outputDir, "dotnet", settings);

        _ = result.Success.Should().BeTrue();
        _ = result.ExportedPaths.Should().NotBeEmpty();
        _ = result.Summary.Should().NotBeNull();
        _ = result.Summary.Should().Contain("CPU avg:");
    }

    [Fact]
    public void BuildsSummaryWithNoMetrics()
    {
        string summary = DotnetCountersCollectorPlugin.BuildSummary(metrics: null);

        _ = summary.Should().Be("No dotnet-counters metrics available");
    }

    [Fact]
    public void BuildsSummaryWithPartialMetrics()
    {
        // Metrics with only CPU data
        string json = """
            {
                "Runtime": {
                    "CpuUsage": { "Avg": 25.0, "Min": 10.0, "Max": 40.0, "Last": 30.0, "Samples": 50 }
                }
            }
            """;
        JsonElement element = JsonSerializer.Deserialize<JsonElement>(json);

        string summary = DotnetCountersCollectorPlugin.BuildSummary(element);

        _ = summary.Should().Contain("CPU avg: 25%");
        _ = summary.Should().Contain("GC heap max: N/A");
    }
}
