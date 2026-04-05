using FluentAssertions;

using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Diagnostics.Collection;
using Hone.Diagnostics.Discovery;
using Hone.TestInfrastructure;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Diagnostics.Tests.Collection;

public sealed class DiagnosticCollectionOrchestratorTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private readonly IHoneEventSink _eventSink = Substitute.For<IHoneEventSink>();

    // ── GetGroups ────────────────────────────────────────────────────────

    [Fact]
    public void MultiPassScheduling_DifferentGroupsSeparated()
    {
        DiagnosticCollectionOrchestrator sut = CreateOrchestrator();

        IReadOnlyList<DiscoveredCollector> collectors =
        [
            MakeCollector("etw-cpu-collector", group: "etw-cpu"),
            MakeCollector("etw-gc-collector", group: "etw-gc"),
        ];

        IReadOnlyDictionary<string, IReadOnlyList<DiscoveredCollector>> groups = sut.GetGroups(collectors);

        _ = groups.Should().HaveCount(2);
        _ = groups.Should().ContainKey("etw-cpu");
        _ = groups.Should().ContainKey("etw-gc");
        _ = groups["etw-cpu"].Should().ContainSingle()
            .Which.Name.Should().Be("etw-cpu-collector");
        _ = groups["etw-gc"].Should().ContainSingle()
            .Which.Name.Should().Be("etw-gc-collector");
    }

    [Fact]
    public void DefaultGroupCollectors_RunInEveryPass()
    {
        DiagnosticCollectionOrchestrator sut = CreateOrchestrator();

        IReadOnlyList<DiscoveredCollector> collectors =
        [
            MakeCollector("dotnet-counters", group: "default"),
            MakeCollector("etw-cpu-collector", group: "etw-cpu"),
            MakeCollector("etw-gc-collector", group: "etw-gc"),
        ];

        IReadOnlyDictionary<string, IReadOnlyList<DiscoveredCollector>> groups = sut.GetGroups(collectors);

        _ = groups.Should().HaveCount(2);

        // default-group collectors appear in every non-default group
        _ = groups["etw-cpu"].Select(c => c.Name).Should()
            .BeEquivalentTo("etw-cpu-collector", "dotnet-counters");
        _ = groups["etw-gc"].Select(c => c.Name).Should()
            .BeEquivalentTo("etw-gc-collector", "dotnet-counters");
    }

    [Fact]
    public void GetGroups_OnlyDefaultCollectors_SingleGroup()
    {
        DiagnosticCollectionOrchestrator sut = CreateOrchestrator();

        IReadOnlyList<DiscoveredCollector> collectors =
        [
            MakeCollector("dotnet-counters", group: "default"),
            MakeCollector("event-pipe", group: "default"),
        ];

        IReadOnlyDictionary<string, IReadOnlyList<DiscoveredCollector>> groups = sut.GetGroups(collectors);

        _ = groups.Should().ContainSingle()
            .Which.Key.Should().Be("default");
        _ = groups["default"].Should().HaveCount(2);
    }

    // ── StartAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task Start_CreatesOutputSubdirectories()
    {
        ICollectorPlugin plugin = CreateSuccessPlugin();
        DiagnosticCollectionOrchestrator sut = CreateOrchestrator(("my-collector", plugin));

        string outputDir = Path.Combine(TempDir, "output");

        CollectionStartResult result = await sut.StartAsync(
            [MakeCollector("my-collector")],
            processId: 1234,
            outputDir: outputDir);

        _ = result.Success.Should().BeTrue();
        _ = Directory.Exists(Path.Combine(outputDir, "my-collector")).Should().BeTrue();
    }

    [Fact]
    public async Task CollectorFailure_OtherCollectorsContinue()
    {
        ICollectorPlugin failPlugin = Substitute.For<ICollectorPlugin>();
        _ = failPlugin.StartAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CollectorSettings>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        ICollectorPlugin goodPlugin = CreateSuccessPlugin();

        DiagnosticCollectionOrchestrator sut = CreateOrchestrator(
            ("fail-collector", failPlugin),
            ("good-collector", goodPlugin));

        string outputDir = Path.Combine(TempDir, "output");

        CollectionStartResult result = await sut.StartAsync(
            [MakeCollector("fail-collector"), MakeCollector("good-collector")],
            processId: 1234,
            outputDir: outputDir);

        _ = result.Success.Should().BeFalse();
        _ = result.Handles.Should().ContainKey("good-collector");
        _ = result.Handles.Should().NotContainKey("fail-collector");
    }

    [Fact]
    public async Task CollectorSubset_FiltersCorrectly()
    {
        ICollectorPlugin pluginA = CreateSuccessPlugin();
        ICollectorPlugin pluginB = CreateSuccessPlugin();

        DiagnosticCollectionOrchestrator sut = CreateOrchestrator(
            ("collector-a", pluginA),
            ("collector-b", pluginB));

        string outputDir = Path.Combine(TempDir, "output");

        CollectionStartResult result = await sut.StartAsync(
            [MakeCollector("collector-a"), MakeCollector("collector-b")],
            processId: 1234,
            outputDir: outputDir,
            collectorSubset: new HashSet<string>(StringComparer.Ordinal) { "collector-a" });

        _ = result.Success.Should().BeTrue();
        _ = result.Handles.Should().ContainKey("collector-a");
        _ = result.Handles.Should().NotContainKey("collector-b");

        // Only collector-a should have been called
        _ = await pluginA.Received(1).StartAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CollectorSettings>(), Arg.Any<CancellationToken>());
        _ = await pluginB.DidNotReceive().StartAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CollectorSettings>(), Arg.Any<CancellationToken>());
    }

    // ── StopAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task Stop_SkipsCollectorsWithoutHandles()
    {
        ICollectorPlugin pluginA = Substitute.For<ICollectorPlugin>();
        _ = pluginA.StopAsync(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(new CollectorArtifacts(Success: true, ArtifactPaths: ["artifact.etl"]));

        ICollectorPlugin pluginB = Substitute.For<ICollectorPlugin>();

        DiagnosticCollectionOrchestrator sut = CreateOrchestrator(
            ("collector-a", pluginA),
            ("collector-b", pluginB));

        // Only collector-a has a handle
        var handles = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["collector-a"] = "handle-a",
        };

        CollectionStopResult result = await sut.StopAsync(
            handles,
            [MakeCollector("collector-a"), MakeCollector("collector-b")]);

        _ = result.Success.Should().BeTrue();
        _ = result.ArtifactMap.Should().ContainKey("collector-a");
        _ = result.ArtifactMap.Should().NotContainKey("collector-b");

        // collector-b's StopAsync should never be called
        _ = await pluginB.DidNotReceive().StopAsync(Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    // ── ExportAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task Export_PassesThroughExtraProperties()
    {
        var extras = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["CpuStacksPath"] = @"C:\output\stacks.json",
            ["GcReportPath"] = @"C:\output\gc-report.html",
        };

        ICollectorPlugin plugin = Substitute.For<ICollectorPlugin>();
        _ = plugin.ExportAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<CollectorSettings>(), Arg.Any<CancellationToken>())
            .Returns(new CollectorExportResult(
                Success: true,
                ExportedPaths: ["export.json"],
                Summary: "All good",
                ExtraProperties: extras));

        DiagnosticCollectionOrchestrator sut = CreateOrchestrator(("cpu-collector", plugin));

        var artifactMap = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["cpu-collector"] = ["trace.etl"],
        };

        string outputDir = Path.Combine(TempDir, "export-output");

        CollectionExportResult result = await sut.ExportAsync(
            artifactMap,
            [MakeCollector("cpu-collector")],
            outputDir,
            processName: "dotnet");

        _ = result.Success.Should().BeTrue();
        _ = result.CollectorData.Should().ContainKey("cpu-collector");

        CollectorExportResult exportResult = result.CollectorData["cpu-collector"];
        _ = exportResult.ExportedPaths.Should().Contain("export.json");
        _ = exportResult.Summary.Should().Be("All good");
        _ = exportResult.ExtraProperties.Should().ContainKey("CpuStacksPath")
            .WhoseValue.Should().Be(@"C:\output\stacks.json");
        _ = exportResult.ExtraProperties.Should().ContainKey("GcReportPath")
            .WhoseValue.Should().Be(@"C:\output\gc-report.html");
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

    private DiagnosticCollectionOrchestrator CreateOrchestrator(
        params (string Name, ICollectorPlugin Plugin)[] plugins)
    {
        var registry = new Dictionary<string, ICollectorPlugin>(StringComparer.Ordinal);
        foreach ((string name, ICollectorPlugin plugin) in plugins)
        {
            registry[name] = plugin;
        }

        return new DiagnosticCollectionOrchestrator(registry, _eventSink);
    }

    private static ICollectorPlugin CreateSuccessPlugin()
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
            .Returns(new CollectorExportResult(Success: true, ExportedPaths: ["export.json"], Summary: "OK"));
        return plugin;
    }
}
