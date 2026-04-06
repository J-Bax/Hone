using System.Text.Json;

using FluentAssertions;

using Hone.Agents.Loop.Analysis;
using Hone.Core.Config;
using Hone.Core.Models;
using Hone.TestInfrastructure;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Agents.Loop.Tests.Analysis;

public sealed class AnalysisContextBuilderTests(ITestOutputHelper output) : HoneTestBase(output)
{
    // ── Source file collection ────────────────────────────────────────────

    [Fact]
    public async Task ContextBuilder_CollectsSourcePaths()
    {
        string targetDir = CreateTargetDir("proj", b => b
            .AddFile("myapi/Controllers/ValuesController.cs", "// controller")
            .AddFile("myapi/Models/Item.cs", "// model")
            .AddFile("myapi/wwwroot/index.html", "<!-- html -->"));

        var config = new HoneConfig(
            Api: new ApiConfig(
                ProjectPath: "myapi",
                SourceCodePaths: ["Controllers", "Models"],
                SourceFileGlob: "*.cs"));

        AnalysisContext result = await AnalysisContextBuilder.BuildAsync(
            targetDir, config, counters: null, previousRcaExplanation: null, diagnosticReports: null);

        _ = result.SourceFilePaths.Should().HaveCount(2);
        _ = result.SourceFilePaths.Should().Contain(p => p.Contains("ValuesController.cs", StringComparison.Ordinal));
        _ = result.SourceFilePaths.Should().Contain(p => p.Contains("Item.cs", StringComparison.Ordinal));
        _ = result.SourceFilePaths.Should().NotContain(p => p.Contains("index.html", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContextBuilder_CollectsSourcePaths_RecursesSubdirectories()
    {
        string targetDir = CreateTargetDir("proj", b => b
            .AddFile("myapi/Controllers/v1/UsersController.cs", "// nested"));

        var config = new HoneConfig(
            Api: new ApiConfig(
                ProjectPath: "myapi",
                SourceCodePaths: ["Controllers"],
                SourceFileGlob: "*.cs"));

        AnalysisContext result = await AnalysisContextBuilder.BuildAsync(
            targetDir, config, counters: null, previousRcaExplanation: null, diagnosticReports: null);

        _ = result.SourceFilePaths.Should().ContainSingle()
            .Which.Should().Contain("UsersController.cs");
    }

    // ── Counter context ──────────────────────────────────────────────────

    [Fact]
    public async Task ContextBuilder_FormatsCounterMetrics()
    {
        string targetDir = CreateTargetDir("proj", b => b
            .AddDirectory("myapi/Controllers"));

        var counters = new CounterSummary(
            CpuAvg: "42.5%",
            GcHeapMax: "128MB",
            Gen2Collections: "7",
            ThreadPoolMaxThreads: "16");

        var config = new HoneConfig(
            Api: new ApiConfig(ProjectPath: "myapi", SourceCodePaths: ["Controllers"]));

        AnalysisContext result = await AnalysisContextBuilder.BuildAsync(
            targetDir, config, counters, previousRcaExplanation: null, diagnosticReports: null);

        _ = result.CounterContext.Should().Contain("## Runtime Counters");
        _ = result.CounterContext.Should().Contain("CPU avg: 42.5%");
        _ = result.CounterContext.Should().Contain("GC heap max: 128MB");
        _ = result.CounterContext.Should().Contain("Gen2 collections: 7");
        _ = result.CounterContext.Should().Contain("Thread pool max threads: 16");
    }

    [Fact]
    public async Task ContextBuilder_NoCounters_EmptyCounterContext()
    {
        string targetDir = CreateTargetDir("proj", b => b
            .AddDirectory("myapi/Controllers"));

        var config = new HoneConfig(
            Api: new ApiConfig(ProjectPath: "myapi", SourceCodePaths: ["Controllers"]));

        AnalysisContext result = await AnalysisContextBuilder.BuildAsync(
            targetDir, config, counters: null, previousRcaExplanation: null, diagnosticReports: null);

        _ = result.CounterContext.Should().BeEmpty();
    }

    // ── Traffic context ──────────────────────────────────────────────────

    [Fact]
    public async Task ContextBuilder_TrafficContext_IncludesScenarioContent()
    {
        const string ScenarioJs = "export default function() { http.get('/api/items'); }";

        string targetDir = CreateTargetDir("proj", b => b
            .AddDirectory("myapi/Controllers")
            .AddFile("scale-tests/baseline.js", ScenarioJs));

        var config = new HoneConfig(
            Api: new ApiConfig(ProjectPath: "myapi", SourceCodePaths: ["Controllers"]),
            ScaleTest: new ScaleTestConfig(ScenarioPath: "scale-tests/baseline.js"));

        AnalysisContext result = await AnalysisContextBuilder.BuildAsync(
            targetDir, config, counters: null, previousRcaExplanation: null, diagnosticReports: null);

        _ = result.TrafficContext.Should().Contain("## Traffic Distribution (k6 Scenario)");
        _ = result.TrafficContext.Should().Contain(ScenarioJs);
        _ = result.TrafficContext.Should().Contain("```javascript");
    }

    [Fact]
    public async Task ContextBuilder_TrafficContext_MissingFile_ReturnsEmpty()
    {
        string targetDir = CreateTargetDir("proj", b => b
            .AddDirectory("myapi/Controllers"));

        var config = new HoneConfig(
            Api: new ApiConfig(ProjectPath: "myapi", SourceCodePaths: ["Controllers"]),
            ScaleTest: new ScaleTestConfig(ScenarioPath: "nonexistent/scenario.js"));

        AnalysisContext result = await AnalysisContextBuilder.BuildAsync(
            targetDir, config, counters: null, previousRcaExplanation: null, diagnosticReports: null);

        _ = result.TrafficContext.Should().BeEmpty();
    }

    // ── History context ──────────────────────────────────────────────────

    [Fact]
    public async Task ContextBuilder_IncludesHistory()
    {
        const string LogContent = "# Experiment 1\nOptimized serialization\n";

        string targetDir = CreateTargetDir("proj", b => b
            .AddDirectory("myapi/Controllers")
            .AddFile("metadata/experiment-log.md", LogContent));

        var config = new HoneConfig(
            Api: new ApiConfig(
                ProjectPath: "myapi",
                SourceCodePaths: ["Controllers"],
                MetadataPath: "metadata"));

        AnalysisContext result = await AnalysisContextBuilder.BuildAsync(
            targetDir, config, counters: null, previousRcaExplanation: null, diagnosticReports: null);

        _ = result.HistoryContext.Should().Contain("## Previously Tried Optimizations");
        _ = result.HistoryContext.Should().Contain("Optimized serialization");
    }

    [Fact]
    public async Task ContextBuilder_HistoryContext_IncludesQueueJson()
    {
        var queue = new OptimizationQueue(
            GeneratedByExperiment: 1,
            Items: [
                new QueueItem("q1", "Controllers/Items.cs", "Cache DB calls",
                    OpportunityScope.Narrow, QueueItemStatus.Done, TriedByExperiment: 2, Outcome: "improved"),
                new QueueItem("q2", "Data/Repo.cs", "Batch queries",
                    OpportunityScope.Architecture, QueueItemStatus.Pending, TriedByExperiment: null, Outcome: null),
            ]);

        string queueJson = JsonSerializer.Serialize(queue);

        string targetDir = CreateTargetDir("proj", b => b
            .AddDirectory("myapi/Controllers")
            .AddFile("metadata/experiment-queue.json", queueJson));

        var config = new HoneConfig(
            Api: new ApiConfig(
                ProjectPath: "myapi",
                SourceCodePaths: ["Controllers"],
                MetadataPath: "metadata"));

        AnalysisContext result = await AnalysisContextBuilder.BuildAsync(
            targetDir, config, counters: null, previousRcaExplanation: null, diagnosticReports: null);

        _ = result.HistoryContext.Should().Contain("## Known Optimization Queue");
        _ = result.HistoryContext.Should().Contain("[TRIED] `Controllers/Items.cs`");
        _ = result.HistoryContext.Should().Contain("experiment 2");
        _ = result.HistoryContext.Should().Contain("[PENDING] [ARCHITECTURE] `Data/Repo.cs`");
    }

    [Fact]
    public async Task ContextBuilder_HistoryContext_NoQueueMarkdownFallback()
    {
        const string QueueMd = "- Optimize serialization\n- Cache responses\n";

        string targetDir = CreateTargetDir("proj", b => b
            .AddDirectory("myapi/Controllers")
            .AddFile("metadata/experiment-queue.md", QueueMd));

        var config = new HoneConfig(
            Api: new ApiConfig(
                ProjectPath: "myapi",
                SourceCodePaths: ["Controllers"],
                MetadataPath: "metadata"));

        AnalysisContext result = await AnalysisContextBuilder.BuildAsync(
            targetDir, config, counters: null, previousRcaExplanation: null, diagnosticReports: null);

        _ = result.HistoryContext.Should().NotContain("## Known Optimization Queue",
            "markdown queue fallback was removed — only JSON queue is supported");
    }

    [Fact]
    public async Task ContextBuilder_HistoryContext_IncludesPreviousRca()
    {
        string targetDir = CreateTargetDir("proj", b => b
            .AddDirectory("myapi/Controllers"));

        var config = new HoneConfig(
            Api: new ApiConfig(
                ProjectPath: "myapi",
                SourceCodePaths: ["Controllers"],
                MetadataPath: "metadata"));

        AnalysisContext result = await AnalysisContextBuilder.BuildAsync(
            targetDir, config, counters: null,
            previousRcaExplanation: "Switched from Newtonsoft to System.Text.Json",
            diagnosticReports: null);

        _ = result.HistoryContext.Should().Contain("## Last Experiment's Fix");
        _ = result.HistoryContext.Should().Contain("Switched from Newtonsoft to System.Text.Json");
    }

    [Fact]
    public async Task ContextBuilder_HistoryContext_IncludesExperimentTable()
    {
        var runMeta = new RunMetadata(
            TargetName: "SampleApi",
            StartedAt: "2024-01-01T00:00:00Z",
            MachineInfo: null,
            Experiments: [
                new ExperimentMetadata(
                    Experiment: 1,
                    StartedAt: "2024-01-01T00:00:00Z",
                    CompletedAt: "2024-01-01T01:00:00Z",
                    Outcome: ExperimentOutcome.Improved,
                    BranchName: "hone/exp-1",
                    BaseBranch: "main",
                    P95: 12.345,
                    RPS: 567.8,
                    PrNumber: null,
                    PrUrl: null,
                    StaleCount: 0,
                    ConsecutiveFailures: 0),
                new ExperimentMetadata(
                    Experiment: 2,
                    StartedAt: "2024-01-01T01:00:00Z",
                    CompletedAt: null,
                    Outcome: ExperimentOutcome.Stale,
                    BranchName: null,
                    BaseBranch: null,
                    P95: null,
                    RPS: null,
                    PrNumber: null,
                    PrUrl: null,
                    StaleCount: 1,
                    ConsecutiveFailures: 0),
            ]);

        string metaJson = JsonSerializer.Serialize(runMeta);

        string targetDir = CreateTargetDir("proj", b => b
            .AddDirectory("myapi/Controllers")
            .AddFile("results/run-metadata.json", metaJson));

        var config = new HoneConfig(
            Api: new ApiConfig(
                ProjectPath: "myapi",
                SourceCodePaths: ["Controllers"],
                ResultsPath: "results"));

        AnalysisContext result = await AnalysisContextBuilder.BuildAsync(
            targetDir, config, counters: null, previousRcaExplanation: null, diagnosticReports: null);

        _ = result.HistoryContext.Should().Contain("## Experiment History (with metrics)");
        _ = result.HistoryContext.Should().Contain("| Exp | File | Outcome | p95 (ms) | RPS | Branch |");
        _ = result.HistoryContext.Should().Contain("| 1 | — | Improved | 12.3 | 567.8 | hone/exp-1 |");
        _ = result.HistoryContext.Should().Contain("| 2 | — | Stale | N/A | N/A | — |");
        _ = result.HistoryContext.Should().Contain("Do NOT re-attempt");
    }

    // ── Profiling context ────────────────────────────────────────────────

    [Fact]
    public async Task ContextBuilder_IncludesDiagnosticReports()
    {
        string targetDir = CreateTargetDir("proj", b => b
            .AddDirectory("myapi/Controllers"));

        var config = new HoneConfig(
            Api: new ApiConfig(ProjectPath: "myapi", SourceCodePaths: ["Controllers"]));

        var reports = new Dictionary<string, AnalyzerReport>(StringComparer.Ordinal)
        {
            ["CpuProfiler"] = new(Success: true, Report: "{\"hotPath\":\"/api/items\"}", Summary: null, PromptPath: null, ResponsePath: null),
            ["AllocAnalyzer"] = new(Success: true, Report: null, Summary: "High allocations in Repo.Get", PromptPath: null, ResponsePath: null),
        };

        AnalysisContext result = await AnalysisContextBuilder.BuildAsync(
            targetDir, config, counters: null, previousRcaExplanation: null, diagnosticReports: reports);

        _ = result.ProfilingContext.Should().Contain("## Diagnostic Profiling Reports");
        _ = result.ProfilingContext.Should().Contain("### AllocAnalyzer");
        _ = result.ProfilingContext.Should().Contain("High allocations in Repo.Get");
        _ = result.ProfilingContext.Should().Contain("### CpuProfiler");
        _ = result.ProfilingContext.Should().Contain("{\"hotPath\":\"/api/items\"}");
        _ = result.ProfilingContext.Should().Contain("```json");
    }

    [Fact]
    public async Task ContextBuilder_NoDiagnostics_EmptyProfilingContext()
    {
        string targetDir = CreateTargetDir("proj", b => b
            .AddDirectory("myapi/Controllers"));

        var config = new HoneConfig(
            Api: new ApiConfig(ProjectPath: "myapi", SourceCodePaths: ["Controllers"]));

        AnalysisContext result = await AnalysisContextBuilder.BuildAsync(
            targetDir, config, counters: null, previousRcaExplanation: null, diagnosticReports: null);

        _ = result.ProfilingContext.Should().BeEmpty();
    }

    [Fact]
    public async Task ContextBuilder_DiagnosticReports_SortedByName()
    {
        string targetDir = CreateTargetDir("proj", b => b
            .AddDirectory("myapi/Controllers"));

        var config = new HoneConfig(
            Api: new ApiConfig(ProjectPath: "myapi", SourceCodePaths: ["Controllers"]));

        var reports = new Dictionary<string, AnalyzerReport>(StringComparer.Ordinal)
        {
            ["Zeta"] = new(Success: true, Report: "z-data", Summary: null, PromptPath: null, ResponsePath: null),
            ["Alpha"] = new(Success: true, Report: "a-data", Summary: null, PromptPath: null, ResponsePath: null),
        };

        AnalysisContext result = await AnalysisContextBuilder.BuildAsync(
            targetDir, config, counters: null, previousRcaExplanation: null, diagnosticReports: reports);

        int alphaPos = result.ProfilingContext.IndexOf("### Alpha", StringComparison.Ordinal);
        int zetaPos = result.ProfilingContext.IndexOf("### Zeta", StringComparison.Ordinal);
        _ = alphaPos.Should().BeLessThan(zetaPos, "reports should be sorted alphabetically");
    }
}
