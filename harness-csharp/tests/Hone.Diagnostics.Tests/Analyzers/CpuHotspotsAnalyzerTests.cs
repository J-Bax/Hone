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

public sealed class CpuHotspotsAnalyzerTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private const string SampleStacks = """
        MyApp!Controller.Get;MyApp!Service.Query;System.Data!SqlCommand.Execute 500
        MyApp!Controller.Get;MyApp!Service.Validate 200
        MyApp!Middleware.Invoke;MyApp!Auth.Check 150
        MyApp!Controller.Get;MyApp!Cache.Lookup 80
        System.Threading!ThreadPool.Dispatch 50
        """;

    private const string AgentJsonResponse = """
        ```json
        {
            "summary": "CPU hotspot in SqlCommand.Execute consuming 50% of samples",
            "hotspots": [
                { "method": "System.Data!SqlCommand.Execute", "samples": 500 }
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
    public async Task BuildsCorrectPrompt()
    {
        string stacksPath = Path.Combine(TempDir, "stacks.txt");
        await File.WriteAllTextAsync(stacksPath, SampleStacks);
        string outputDir = Path.Combine(TempDir, "output");

        IAgentRunner runner = Substitute.For<IAgentRunner>();
        _ = runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult(Success: true, Output: AgentJsonResponse, TimedOut: false, ExitCode: 0));

        var collectorData = new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal)
        {
            ["perfview-cpu"] = new CollectorExportResult(
                Success: true,
                ExportedPaths: [stacksPath],
                ExtraProperties: new Dictionary<string, object?>(StringComparer.Ordinal) { ["CpuStacksPath"] = stacksPath }),
        };

        var analyzer = new CpuHotspotsAnalyzer(runner);
        _ = await analyzer.AnalyzeAsync(CreateContext(outputDir, collectorData, CreateMetrics()));

        var captured = (AgentInvocation)runner.ReceivedCalls().First().GetArguments()[0]!;

        _ = captured.Prompt.Should().Contain("CPU sampling data from PerfView");
        _ = captured.Prompt.Should().Contain("SqlCommand.Execute");
        _ = captured.Prompt.Should().Contain("p95 Latency: 120.5ms");
        _ = captured.Prompt.Should().Contain("Requests/sec: 250.3");
        _ = captured.Prompt.Should().Contain("Error rate: 0.20%");
        _ = captured.AgentName.Should().Be("hone-cpu-profiler");
    }

    [Fact]
    public async Task TruncatesToMaxStacks()
    {
        // Create a stacks file with 10 lines
        var lines = new List<string>();
        for (int i = 1; i <= 10; i++)
        {
            lines.Add($"MyApp!Method{i} {i * 100}");
        }

        string stacksPath = Path.Combine(TempDir, "stacks.txt");
        await File.WriteAllTextAsync(stacksPath, string.Join("\n", lines));
        string outputDir = Path.Combine(TempDir, "output");

        IAgentRunner runner = Substitute.For<IAgentRunner>();
        _ = runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult(Success: true, Output: "{}", TimedOut: false, ExitCode: 0));

        var collectorData = new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal)
        {
            ["perfview-cpu"] = new CollectorExportResult(
                Success: true,
                ExportedPaths: [stacksPath]),
        };

        // Set MaxStacks to 3
        var settings = new Dictionary<string, object?>(StringComparer.Ordinal) { ["MaxStacks"] = 3 };

        var analyzer = new CpuHotspotsAnalyzer(runner);
        _ = await analyzer.AnalyzeAsync(CreateContext(outputDir, collectorData, settings: settings));

        var captured = (AgentInvocation)runner.ReceivedCalls().First().GetArguments()[0]!;

        // Should contain only top 3 stacks by count (1000, 900, 800)
        _ = captured.Prompt.Should().Contain("top 3 stacks by sample count");
        _ = captured.Prompt.Should().Contain("MyApp!Method10 1000");
        _ = captured.Prompt.Should().Contain("MyApp!Method9 900");
        _ = captured.Prompt.Should().Contain("MyApp!Method8 800");
        _ = captured.Prompt.Should().NotContain("MyApp!Method7 700");
    }

    [Fact]
    public async Task MissingCollectorData_ReturnsFailure()
    {
        string outputDir = Path.Combine(TempDir, "output");
        IAgentRunner runner = Substitute.For<IAgentRunner>();

        var analyzer = new CpuHotspotsAnalyzer(runner);
        AnalyzerResult result = await analyzer.AnalyzeAsync(CreateContext(outputDir));

        _ = result.Success.Should().BeFalse();
        _ = result.Summary.Should().Contain("No CPU collector data");
        _ = runner.DidNotReceive().InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingStacksFile_ReturnsFailure()
    {
        string outputDir = Path.Combine(TempDir, "output");
        IAgentRunner runner = Substitute.For<IAgentRunner>();

        var collectorData = new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal)
        {
            ["perfview-cpu"] = new CollectorExportResult(
                Success: true,
                ExportedPaths: [Path.Combine(TempDir, "nonexistent.txt")]),
        };

        var analyzer = new CpuHotspotsAnalyzer(runner);
        AnalyzerResult result = await analyzer.AnalyzeAsync(CreateContext(outputDir, collectorData));

        _ = result.Success.Should().BeFalse();
        _ = result.Summary.Should().Contain("Folded stacks file not found");
    }

    [Fact]
    public async Task ParsesAgentResponse()
    {
        string stacksPath = Path.Combine(TempDir, "stacks.txt");
        await File.WriteAllTextAsync(stacksPath, SampleStacks);
        string outputDir = Path.Combine(TempDir, "output");

        IAgentRunner runner = Substitute.For<IAgentRunner>();
        _ = runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult(Success: true, Output: AgentJsonResponse, TimedOut: false, ExitCode: 0));

        var collectorData = new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal)
        {
            ["perfview-cpu"] = new CollectorExportResult(
                Success: true,
                ExportedPaths: [stacksPath]),
        };

        var analyzer = new CpuHotspotsAnalyzer(runner);
        AnalyzerResult result = await analyzer.AnalyzeAsync(CreateContext(outputDir, collectorData, CreateMetrics()));

        _ = result.Success.Should().BeTrue();
        _ = result.Report.Should().NotBeNull();
        _ = result.Summary.Should().Contain("SqlCommand.Execute");
    }

    [Fact]
    public async Task SavesPromptAndResponse()
    {
        string stacksPath = Path.Combine(TempDir, "stacks.txt");
        await File.WriteAllTextAsync(stacksPath, SampleStacks);
        string outputDir = Path.Combine(TempDir, "output");

        IAgentRunner runner = Substitute.For<IAgentRunner>();
        _ = runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult(Success: true, Output: AgentJsonResponse, TimedOut: false, ExitCode: 0));

        var collectorData = new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal)
        {
            ["perfview-cpu"] = new CollectorExportResult(
                Success: true,
                ExportedPaths: [stacksPath]),
        };

        var analyzer = new CpuHotspotsAnalyzer(runner);
        AnalyzerResult result = await analyzer.AnalyzeAsync(CreateContext(outputDir, collectorData, CreateMetrics()));

        _ = result.PromptPath.Should().NotBeNull();
        _ = result.ResponsePath.Should().NotBeNull();
        _ = File.Exists(result.PromptPath).Should().BeTrue();
        _ = File.Exists(result.ResponsePath).Should().BeTrue();

        string promptContent = await File.ReadAllTextAsync(result.PromptPath!);
        _ = promptContent.Should().Contain("CPU sampling data");

        string responseContent = await File.ReadAllTextAsync(result.ResponsePath!);
        _ = responseContent.Should().Contain("SqlCommand.Execute");
    }

    [Fact]
    public async Task AgentFailure_ReturnsGracefully()
    {
        string stacksPath = Path.Combine(TempDir, "stacks.txt");
        await File.WriteAllTextAsync(stacksPath, SampleStacks);
        string outputDir = Path.Combine(TempDir, "output");

        IAgentRunner runner = Substitute.For<IAgentRunner>();
        _ = runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Agent process crashed"));

        var collectorData = new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal)
        {
            ["perfview-cpu"] = new CollectorExportResult(
                Success: true,
                ExportedPaths: [stacksPath]),
        };

        var analyzer = new CpuHotspotsAnalyzer(runner);
        AnalyzerResult result = await analyzer.AnalyzeAsync(CreateContext(outputDir, collectorData));

        _ = result.Success.Should().BeFalse();
        _ = result.Summary.Should().Contain("Analysis agent error");
        _ = result.PromptPath.Should().NotBeNull();
        _ = result.ResponsePath.Should().BeNull();
    }

    [Fact]
    public async Task UsesExportedPathsFallback_WhenNoExtraProperties()
    {
        string stacksPath = Path.Combine(TempDir, "stacks.txt");
        await File.WriteAllTextAsync(stacksPath, SampleStacks);
        string outputDir = Path.Combine(TempDir, "output");

        IAgentRunner runner = Substitute.For<IAgentRunner>();
        _ = runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult(Success: true, Output: AgentJsonResponse, TimedOut: false, ExitCode: 0));

        // No ExtraProperties — should fall back to ExportedPaths[0]
        var collectorData = new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal)
        {
            ["perfview-cpu"] = new CollectorExportResult(
                Success: true,
                ExportedPaths: [stacksPath]),
        };

        var analyzer = new CpuHotspotsAnalyzer(runner);
        AnalyzerResult result = await analyzer.AnalyzeAsync(CreateContext(outputDir, collectorData, CreateMetrics()));

        _ = result.Success.Should().BeTrue();
    }
}
