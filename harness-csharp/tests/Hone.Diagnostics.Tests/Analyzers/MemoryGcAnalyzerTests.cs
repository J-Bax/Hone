using FluentAssertions;

using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Diagnostics.Analyzers;
using Hone.TestInfrastructure;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Diagnostics.Tests.Analyzers;

public sealed class MemoryGcAnalyzerTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private const string SampleGcReport = """
        GC Statistics Summary
        =====================
        Total GC Count: 42
        Gen0: 30, Gen1: 10, Gen2: 2
        Max Pause: 45ms
        Total Pause: 320ms
        Peak Heap Size: 512MB
        """;

    private const string SampleAllocTypes = """
        System.String 15000
        System.Byte[] 8000
        MyApp.Models.Request 3000
        """;

    private const string AgentJsonResponse = """
        ```json
        {
            "summary": "High Gen0 GC pressure from System.String allocations",
            "findings": [
                { "issue": "Excessive string allocations", "impact": "high" }
            ]
        }
        ```
        """;

    private static AnalyzerContext CreateContext(
        string outputDir,
        IReadOnlyDictionary<string, CollectorExportResult>? collectorData = null,
        MetricSet? metrics = null,
        IReadOnlyDictionary<string, object?>? settings = null)
    {
        return new AnalyzerContext(
            CollectorData: collectorData ?? new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal),
            CurrentMetrics: metrics,
            Experiment: 1,
            Settings: settings ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            OutputDir: outputDir);
    }

    private static MetricSet CreateMetrics() =>
        new(
            Timestamp: "2025-01-01T00:00:00Z",
            Experiment: 1,
            Run: 1,
            HttpReqDuration: new HttpReqDurationMetrics(Avg: 50.0, P50: 45.0, P90: 90.0, P95: 120.5, P99: 200.0, Max: 500.0),
            HttpReqs: new HttpReqCountMetrics(Count: 5000, Rate: 250.3),
            HttpReqFailed: new HttpReqFailedMetrics(Count: 10, Rate: 0.002),
            SummaryPath: null);

    [Fact]
    public async Task IncludesGcReport()
    {
        string gcPath = Path.Combine(TempDir, "gc-report.txt");
        await File.WriteAllTextAsync(gcPath, SampleGcReport);
        string outputDir = Path.Combine(TempDir, "output");

        IAgentRunner runner = Substitute.For<IAgentRunner>();
        _ = runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult(Success: true, Output: AgentJsonResponse, TimedOut: false, ExitCode: 0));

        var collectorData = new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal)
        {
            ["perfview-gc"] = new CollectorExportResult(
                Success: true,
                ExportedPaths: [gcPath],
                ExtraProperties: new Dictionary<string, object?>(StringComparer.Ordinal) { ["GcReportPath"] = gcPath }),
        };

        var analyzer = new MemoryGcAnalyzer(runner);
        _ = await analyzer.AnalyzeAsync(CreateContext(outputDir, collectorData, CreateMetrics()));

        var captured = (AgentInvocation)runner.ReceivedCalls().First().GetArguments()[0]!;

        _ = captured.Prompt.Should().Contain("GC and memory data from PerfView");
        _ = captured.Prompt.Should().Contain("Total GC Count: 42");
        _ = captured.Prompt.Should().Contain("Peak Heap Size: 512MB");
        _ = captured.Prompt.Should().Contain("p95 Latency: 120.5ms");
        _ = captured.AgentName.Should().Be("hone-memory-profiler");
    }

    [Fact]
    public async Task IncludesAllocTypes_WhenAvailable()
    {
        string gcPath = Path.Combine(TempDir, "gc-report.txt");
        await File.WriteAllTextAsync(gcPath, SampleGcReport);
        string allocPath = Path.Combine(TempDir, "alloc-types.txt");
        await File.WriteAllTextAsync(allocPath, SampleAllocTypes);
        string outputDir = Path.Combine(TempDir, "output");

        IAgentRunner runner = Substitute.For<IAgentRunner>();
        _ = runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult(Success: true, Output: AgentJsonResponse, TimedOut: false, ExitCode: 0));

        var collectorData = new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal)
        {
            ["perfview-gc"] = new CollectorExportResult(
                Success: true,
                ExportedPaths: [gcPath],
                ExtraProperties: new Dictionary<string, object?>(StringComparer.Ordinal) { ["GcReportPath"] = gcPath }),
            ["perfview-cpu"] = new CollectorExportResult(
                Success: true,
                ExportedPaths: ["stacks.txt", allocPath],
                ExtraProperties: new Dictionary<string, object?>(StringComparer.Ordinal) { ["AllocTypesPath"] = allocPath }),
        };

        var analyzer = new MemoryGcAnalyzer(runner);
        _ = await analyzer.AnalyzeAsync(CreateContext(outputDir, collectorData, CreateMetrics()));

        var captured = (AgentInvocation)runner.ReceivedCalls().First().GetArguments()[0]!;

        _ = captured.Prompt.Should().Contain("Top Allocating Types");
        _ = captured.Prompt.Should().Contain("System.String 15000");
        _ = captured.Prompt.Should().Contain("MyApp.Models.Request 3000");
    }

    [Fact]
    public async Task OmitsAllocSection_WhenNotAvailable()
    {
        string gcPath = Path.Combine(TempDir, "gc-report.txt");
        await File.WriteAllTextAsync(gcPath, SampleGcReport);
        string outputDir = Path.Combine(TempDir, "output");

        IAgentRunner runner = Substitute.For<IAgentRunner>();
        _ = runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult(Success: true, Output: AgentJsonResponse, TimedOut: false, ExitCode: 0));

        var collectorData = new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal)
        {
            ["perfview-gc"] = new CollectorExportResult(
                Success: true,
                ExportedPaths: [gcPath]),
        };

        var analyzer = new MemoryGcAnalyzer(runner);
        _ = await analyzer.AnalyzeAsync(CreateContext(outputDir, collectorData, CreateMetrics()));

        var captured = (AgentInvocation)runner.ReceivedCalls().First().GetArguments()[0]!;

        _ = captured.Prompt.Should().NotContain("Top Allocating Types");
    }

    [Fact]
    public async Task MissingGcData_ReturnsFailure()
    {
        string outputDir = Path.Combine(TempDir, "output");
        IAgentRunner runner = Substitute.For<IAgentRunner>();

        var analyzer = new MemoryGcAnalyzer(runner);
        AnalyzerResult result = await analyzer.AnalyzeAsync(CreateContext(outputDir));

        _ = result.Success.Should().BeFalse();
        _ = result.Summary.Should().Contain("No GC collector data");
        _ = runner.DidNotReceive().InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingGcReportFile_ReturnsFailure()
    {
        string outputDir = Path.Combine(TempDir, "output");
        IAgentRunner runner = Substitute.For<IAgentRunner>();

        var collectorData = new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal)
        {
            ["perfview-gc"] = new CollectorExportResult(
                Success: true,
                ExportedPaths: [Path.Combine(TempDir, "nonexistent.txt")]),
        };

        var analyzer = new MemoryGcAnalyzer(runner);
        AnalyzerResult result = await analyzer.AnalyzeAsync(CreateContext(outputDir, collectorData));

        _ = result.Success.Should().BeFalse();
        _ = result.Summary.Should().Contain("GC report file not found");
    }

    [Fact]
    public async Task AgentFailure_ReturnsGracefully()
    {
        string gcPath = Path.Combine(TempDir, "gc-report.txt");
        await File.WriteAllTextAsync(gcPath, SampleGcReport);
        string outputDir = Path.Combine(TempDir, "output");

        IAgentRunner runner = Substitute.For<IAgentRunner>();
        _ = runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Agent process crashed"));

        var collectorData = new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal)
        {
            ["perfview-gc"] = new CollectorExportResult(
                Success: true,
                ExportedPaths: [gcPath]),
        };

        var analyzer = new MemoryGcAnalyzer(runner);
        AnalyzerResult result = await analyzer.AnalyzeAsync(CreateContext(outputDir, collectorData));

        _ = result.Success.Should().BeFalse();
        _ = result.Summary.Should().Contain("Analysis agent error");
        _ = result.PromptPath.Should().NotBeNull();
        _ = result.ResponsePath.Should().BeNull();
    }

    [Fact]
    public async Task ParsesAgentResponse()
    {
        string gcPath = Path.Combine(TempDir, "gc-report.txt");
        await File.WriteAllTextAsync(gcPath, SampleGcReport);
        string outputDir = Path.Combine(TempDir, "output");

        IAgentRunner runner = Substitute.For<IAgentRunner>();
        _ = runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult(Success: true, Output: AgentJsonResponse, TimedOut: false, ExitCode: 0));

        var collectorData = new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal)
        {
            ["perfview-gc"] = new CollectorExportResult(
                Success: true,
                ExportedPaths: [gcPath]),
        };

        var analyzer = new MemoryGcAnalyzer(runner);
        AnalyzerResult result = await analyzer.AnalyzeAsync(CreateContext(outputDir, collectorData, CreateMetrics()));

        _ = result.Success.Should().BeTrue();
        _ = result.Report.Should().NotBeNull();
        _ = result.Summary.Should().Contain("System.String");
    }

    [Fact]
    public async Task SavesPromptAndResponse()
    {
        string gcPath = Path.Combine(TempDir, "gc-report.txt");
        await File.WriteAllTextAsync(gcPath, SampleGcReport);
        string outputDir = Path.Combine(TempDir, "output");

        IAgentRunner runner = Substitute.For<IAgentRunner>();
        _ = runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult(Success: true, Output: AgentJsonResponse, TimedOut: false, ExitCode: 0));

        var collectorData = new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal)
        {
            ["perfview-gc"] = new CollectorExportResult(
                Success: true,
                ExportedPaths: [gcPath]),
        };

        var analyzer = new MemoryGcAnalyzer(runner);
        AnalyzerResult result = await analyzer.AnalyzeAsync(CreateContext(outputDir, collectorData, CreateMetrics()));

        _ = result.PromptPath.Should().NotBeNull();
        _ = result.ResponsePath.Should().NotBeNull();
        _ = File.Exists(result.PromptPath).Should().BeTrue();
        _ = File.Exists(result.ResponsePath).Should().BeTrue();
    }
}
