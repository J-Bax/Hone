using FluentAssertions;

using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Diagnostics.Collection;
using Hone.Diagnostics.Discovery;
using Hone.Diagnostics.Measurement;
using Hone.TestInfrastructure;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Diagnostics.Tests.Measurement;

public sealed class DiagnosticMeasurementOrchestratorTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private readonly IHoneEventSink _eventSink = Substitute.For<IHoneEventSink>();

    // ── RunCollectionPassAsync ───────────────────────────────────────────

    [Fact]
    public async Task FullCycle_PerPass_StartCollectStopExport()
    {
        // Arrange — track call order to verify start → stop → export sequence
        var callOrder = new List<string>();

        ICollectorPlugin plugin = Substitute.For<ICollectorPlugin>();
        object handle = new();

        _ = plugin.StartAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CollectorSettings>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callOrder.Add("start");
                return new CollectorStartResult(Success: true, Handle: handle);
            });

        _ = plugin.StopAsync(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callOrder.Add("stop");
                return new CollectorArtifacts(Success: true, ArtifactPaths: ["trace.etl"]);
            });

        _ = plugin.ExportAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<CollectorSettings>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callOrder.Add("export");
                return new CollectorExportResult(Success: true, ExportedPaths: ["export.json"], Summary: "OK");
            });

        DiagnosticMeasurementOrchestrator sut = CreateOrchestrator(
            collectorPlugins: [("my-collector", plugin)]);

        string outputDir = Path.Combine(TempDir, "output");

        // Act
        CollectionPassResult result = await sut.RunCollectionPassAsync(
            [MakeCollector("my-collector")],
            groupName: "default",
            collectorNames: new HashSet<string>(StringComparer.Ordinal) { "my-collector" },
            processId: 1234,
            processName: "dotnet",
            outputDir: outputDir,
            workload: () => Task.CompletedTask);

        // Assert — correct lifecycle order
        _ = result.Success.Should().BeTrue();
        _ = callOrder.Should().ContainInOrder("start", "stop", "export");
        _ = result.CollectorData.Should().ContainKey("my-collector");
    }

    [Fact]
    public async Task CollectorFailure_OtherPassesContinue()
    {
        // Arrange — one collector fails to start, another succeeds
        ICollectorPlugin failPlugin = Substitute.For<ICollectorPlugin>();
        _ = failPlugin.StartAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CollectorSettings>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        ICollectorPlugin goodPlugin = CreateSuccessPlugin();

        DiagnosticMeasurementOrchestrator sut = CreateOrchestrator(
            collectorPlugins: [("fail-collector", failPlugin), ("good-collector", goodPlugin)]);

        string outputDir = Path.Combine(TempDir, "output");

        // Act
        CollectionPassResult result = await sut.RunCollectionPassAsync(
            [MakeCollector("fail-collector"), MakeCollector("good-collector")],
            groupName: "default",
            collectorNames: new HashSet<string>(StringComparer.Ordinal) { "fail-collector", "good-collector" },
            processId: 1234,
            processName: "dotnet",
            outputDir: outputDir,
            workload: () => Task.CompletedTask);

        // Assert — partial failure: good collector data is still available
        _ = result.Success.Should().BeFalse();
        _ = result.CollectorData.Should().ContainKey("good-collector");
    }

    [Fact]
    public async Task MultipleGroups_DataMergedCorrectly()
    {
        // Arrange — two passes for different groups
        ICollectorPlugin cpuPlugin = CreateSuccessPlugin("cpu-export.json", "CPU data");
        ICollectorPlugin gcPlugin = CreateSuccessPlugin("gc-export.json", "GC data");
        ICollectorPlugin countersPlugin = CreateSuccessPlugin("counters.json", "Counters");

        DiagnosticMeasurementOrchestrator sut = CreateOrchestrator(
            collectorPlugins: [
                ("perfview-cpu", cpuPlugin),
                ("perfview-gc", gcPlugin),
                ("dotnet-counters", countersPlugin),
            ]);

        string outputDir = Path.Combine(TempDir, "output");

        // Act — pass 1: cpu group
        CollectionPassResult pass1 = await sut.RunCollectionPassAsync(
            [MakeCollector("perfview-cpu", "etw-cpu"), MakeCollector("dotnet-counters")],
            groupName: "etw-cpu",
            collectorNames: new HashSet<string>(StringComparer.Ordinal) { "perfview-cpu", "dotnet-counters" },
            processId: 1234,
            processName: "dotnet",
            outputDir: outputDir,
            workload: () => Task.CompletedTask);

        // Act — pass 2: gc group
        CollectionPassResult pass2 = await sut.RunCollectionPassAsync(
            [MakeCollector("perfview-gc", "etw-gc"), MakeCollector("dotnet-counters")],
            groupName: "etw-gc",
            collectorNames: new HashSet<string>(StringComparer.Ordinal) { "perfview-gc", "dotnet-counters" },
            processId: 5678,
            processName: "dotnet",
            outputDir: outputDir,
            workload: () => Task.CompletedTask);

        // Merge (caller responsibility)
        var merged = new Dictionary<string, CollectorExportResult>(pass1.CollectorData, StringComparer.Ordinal);
        foreach (KeyValuePair<string, CollectorExportResult> kvp in pass2.CollectorData)
        {
            merged[kvp.Key] = kvp.Value;
        }

        // Assert — merged data contains both groups
        _ = pass1.Success.Should().BeTrue();
        _ = pass2.Success.Should().BeTrue();
        _ = merged.Should().ContainKey("perfview-cpu");
        _ = merged.Should().ContainKey("perfview-gc");
        _ = merged.Should().ContainKey("dotnet-counters");
    }

    [Fact]
    public async Task EmptyCollectors_ReturnsEmptyResult()
    {
        DiagnosticMeasurementOrchestrator sut = CreateOrchestrator();

        string outputDir = Path.Combine(TempDir, "output");

        CollectionPassResult result = await sut.RunCollectionPassAsync(
            [],
            groupName: "default",
            collectorNames: new HashSet<string>(StringComparer.Ordinal),
            processId: 1234,
            processName: "dotnet",
            outputDir: outputDir,
            workload: () => Task.CompletedTask);

        // No collectors started → failure but empty data
        _ = result.CollectorData.Should().BeEmpty();
    }

    // ── RunAnalyzersAsync ────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzersRunAfterAllPasses()
    {
        // Arrange — analyzer that requires cpu and gc data
        IAnalyzerPlugin analyzer = Substitute.For<IAnalyzerPlugin>();
        _ = analyzer.Name.Returns("cpu-hotspots");
        _ = analyzer.RequiredCollectors.Returns(["perfview-cpu"]);
        _ = analyzer.AnalyzeAsync(Arg.Any<AnalyzerContext>(), Arg.Any<CancellationToken>())
            .Returns(new AnalyzerResult(Success: true, Summary: "Found 3 hotspots"));

        DiagnosticMeasurementOrchestrator sut = CreateOrchestrator(
            analyzerPlugins: [("cpu-hotspots", analyzer)]);

        // Merged data from two passes
        var mergedData = new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal)
        {
            ["perfview-cpu"] = new(Success: true, ExportedPaths: ["cpu.json"]),
            ["perfview-gc"] = new(Success: true, ExportedPaths: ["gc.json"]),
        };

        string outputDir = Path.Combine(TempDir, "analysis");

        // Act
        DiagnosticAnalysisResult result = await sut.RunAnalyzersAsync(
            [MakeAnalyzer("cpu-hotspots", requiredCollectors: ["perfview-cpu"])],
            mergedData,
            currentMetrics: null,
            experiment: 1,
            outputDir: outputDir);

        // Assert — analyzer ran and received merged data
        _ = result.Success.Should().BeTrue();
        _ = result.Reports.Should().ContainKey("cpu-hotspots");
        _ = result.Reports["cpu-hotspots"].Summary.Should().Be("Found 3 hotspots");

        // Verify analyzer received the full merged collector data
        _ = await analyzer.Received(1).AnalyzeAsync(
            Arg.Is<AnalyzerContext>(ctx => ctx.CollectorData.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnalyzerSkippedWhenRequiredCollectorMissing()
    {
        // Arrange — analyzer requires perfview-cpu, but it's not in the data
        IAnalyzerPlugin analyzer = Substitute.For<IAnalyzerPlugin>();
        _ = analyzer.Name.Returns("cpu-hotspots");
        _ = analyzer.RequiredCollectors.Returns(["perfview-cpu"]);

        DiagnosticMeasurementOrchestrator sut = CreateOrchestrator(
            analyzerPlugins: [("cpu-hotspots", analyzer)]);

        // Merged data does NOT contain perfview-cpu
        var mergedData = new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal)
        {
            ["dotnet-counters"] = new(Success: true, ExportedPaths: ["counters.csv"]),
        };

        string outputDir = Path.Combine(TempDir, "analysis");

        // Act
        DiagnosticAnalysisResult result = await sut.RunAnalyzersAsync(
            [MakeAnalyzer("cpu-hotspots", requiredCollectors: ["perfview-cpu"])],
            mergedData,
            currentMetrics: null,
            experiment: 1,
            outputDir: outputDir);

        // Assert — analyzer was skipped (not called), result still succeeds
        _ = result.Success.Should().BeTrue();
        _ = result.Reports.Should().BeEmpty();
        _ = await analyzer.DidNotReceive().AnalyzeAsync(
            Arg.Any<AnalyzerContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnalyzerFailure_OtherAnalyzersContinue()
    {
        // Arrange — two analyzers: one throws, one succeeds
        IAnalyzerPlugin failAnalyzer = Substitute.For<IAnalyzerPlugin>();
        _ = failAnalyzer.Name.Returns("bad-analyzer");
        _ = failAnalyzer.RequiredCollectors.Returns([]);
        _ = failAnalyzer.AnalyzeAsync(Arg.Any<AnalyzerContext>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("analyzer crash"));

        IAnalyzerPlugin goodAnalyzer = Substitute.For<IAnalyzerPlugin>();
        _ = goodAnalyzer.Name.Returns("good-analyzer");
        _ = goodAnalyzer.RequiredCollectors.Returns([]);
        _ = goodAnalyzer.AnalyzeAsync(Arg.Any<AnalyzerContext>(), Arg.Any<CancellationToken>())
            .Returns(new AnalyzerResult(Success: true, Summary: "All clear"));

        DiagnosticMeasurementOrchestrator sut = CreateOrchestrator(
            analyzerPlugins: [("bad-analyzer", failAnalyzer), ("good-analyzer", goodAnalyzer)]);

        var mergedData = new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal);
        string outputDir = Path.Combine(TempDir, "analysis");

        // Act
        DiagnosticAnalysisResult result = await sut.RunAnalyzersAsync(
            [
                MakeAnalyzer("bad-analyzer"),
                MakeAnalyzer("good-analyzer"),
            ],
            mergedData,
            currentMetrics: null,
            experiment: 1,
            outputDir: outputDir);

        // Assert — failure isolated, good analyzer still ran
        _ = result.Success.Should().BeFalse();
        _ = result.Reports.Should().ContainKey("good-analyzer");
        _ = result.Reports.Should().NotContainKey("bad-analyzer");
    }

    [Fact]
    public async Task AnalyzersRunInParallel()
    {
        // Arrange — both analyzers must reach the same point before either can finish
        using var bothStarted = new CountdownEvent(2);
        var completionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int completedAnalyzers = 0;

        IAnalyzerPlugin analyzerA = Substitute.For<IAnalyzerPlugin>();
        _ = analyzerA.Name.Returns("analyzer-a");
        _ = analyzerA.RequiredCollectors.Returns([]);
        _ = analyzerA.AnalyzeAsync(Arg.Any<AnalyzerContext>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                _ = bothStarted.Signal();
                await completionGate.Task.WaitAsync(callInfo.Arg<CancellationToken>()).ConfigureAwait(false);
                _ = Interlocked.Increment(ref completedAnalyzers);
                return new AnalyzerResult(Success: true, Summary: "A done");
            });

        IAnalyzerPlugin analyzerB = Substitute.For<IAnalyzerPlugin>();
        _ = analyzerB.Name.Returns("analyzer-b");
        _ = analyzerB.RequiredCollectors.Returns([]);
        _ = analyzerB.AnalyzeAsync(Arg.Any<AnalyzerContext>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                _ = bothStarted.Signal();
                await completionGate.Task.WaitAsync(callInfo.Arg<CancellationToken>()).ConfigureAwait(false);
                _ = Interlocked.Increment(ref completedAnalyzers);
                return new AnalyzerResult(Success: true, Summary: "B done");
            });

        DiagnosticMeasurementOrchestrator sut = CreateOrchestrator(
            analyzerPlugins: [("analyzer-a", analyzerA), ("analyzer-b", analyzerB)]);

        var mergedData = new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal);
        string outputDir = Path.Combine(TempDir, "analysis");

        // Act
        Task<DiagnosticAnalysisResult> runTask = sut.RunAnalyzersAsync(
            [MakeAnalyzer("analyzer-a"), MakeAnalyzer("analyzer-b")],
            mergedData,
            currentMetrics: null,
            experiment: 1,
            outputDir: outputDir);

        bool bothReachedBarrier = bothStarted.Wait(TimeSpan.FromSeconds(1));
        _ = completionGate.TrySetResult();

        DiagnosticAnalysisResult result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert — neither analyzer could finish until both had started
        _ = bothReachedBarrier.Should().BeTrue();
        _ = result.Success.Should().BeTrue();
        _ = result.Reports.Should().ContainKey("analyzer-a");
        _ = result.Reports.Should().ContainKey("analyzer-b");
        _ = result.Reports["analyzer-a"].Summary.Should().Be("A done");
        _ = result.Reports["analyzer-b"].Summary.Should().Be("B done");
        _ = completedAnalyzers.Should().Be(2);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static DiscoveredCollector MakeCollector(string name, string group = "default")
    {
        var metadata = new CollectorMetadata(
            Name: name,
            Description: $"Test {name}",
            Group: group,
            RequiresAdmin: false,
            OverheadImpact: null,
            DefaultSettings: new Dictionary<string, object?>(StringComparer.Ordinal));

        return new DiscoveredCollector(
            Name: name,
            Directory: $@"C:\plugins\{name}",
            Metadata: metadata,
            MergedSettings: new CollectorSettings(new Dictionary<string, object?>(StringComparer.Ordinal)),
            Group: group);
    }

    private static DiscoveredAnalyzer MakeAnalyzer(
        string name,
        IReadOnlyList<string>? requiredCollectors = null)
    {
        var metadata = new AnalyzerMetadata(
            Name: name,
            Description: $"Test {name}",
            RequiredCollectors: requiredCollectors ?? [],
            OptionalCollectors: null,
            AgentName: null,
            DefaultSettings: new Dictionary<string, object?>(StringComparer.Ordinal));

        return new DiscoveredAnalyzer(
            Name: name,
            Directory: $@"C:\plugins\{name}",
            Metadata: metadata,
            MergedSettings: new Dictionary<string, object?>(StringComparer.Ordinal));
    }

    private DiagnosticMeasurementOrchestrator CreateOrchestrator(
        (string Name, ICollectorPlugin Plugin)[]? collectorPlugins = null,
        (string Name, IAnalyzerPlugin Plugin)[]? analyzerPlugins = null)
    {
        var collectorRegistry = new Dictionary<string, ICollectorPlugin>(StringComparer.Ordinal);
        if (collectorPlugins is not null)
        {
            foreach ((string name, ICollectorPlugin plugin) in collectorPlugins)
            {
                collectorRegistry[name] = plugin;
            }
        }

        var analyzerRegistry = new Dictionary<string, IAnalyzerPlugin>(StringComparer.Ordinal);
        if (analyzerPlugins is not null)
        {
            foreach ((string name, IAnalyzerPlugin plugin) in analyzerPlugins)
            {
                analyzerRegistry[name] = plugin;
            }
        }

        var collectionOrchestrator = new DiagnosticCollectionOrchestrator(collectorRegistry, _eventSink);

        return new DiagnosticMeasurementOrchestrator(
            collectionOrchestrator,
            analyzerRegistry,
            _eventSink);
    }

    private static ICollectorPlugin CreateSuccessPlugin(
        string exportPath = "export.json",
        string summary = "OK")
    {
        object handle = new();
        ICollectorPlugin plugin = Substitute.For<ICollectorPlugin>();

        _ = plugin.StartAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CollectorSettings>(), Arg.Any<CancellationToken>())
            .Returns(new CollectorStartResult(Success: true, Handle: handle));
        _ = plugin.StopAsync(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(new CollectorArtifacts(Success: true, ArtifactPaths: ["artifact.etl"]));
        _ = plugin.ExportAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<CollectorSettings>(), Arg.Any<CancellationToken>())
            .Returns(new CollectorExportResult(Success: true, ExportedPaths: [exportPath], Summary: summary));

        return plugin;
    }
}
