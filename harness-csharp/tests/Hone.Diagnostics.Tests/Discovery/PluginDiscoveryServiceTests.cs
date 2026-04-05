using FluentAssertions;

using Hone.Core.Config;
using Hone.Diagnostics.Discovery;
using Hone.TestInfrastructure;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Diagnostics.Tests.Discovery;

public sealed class PluginDiscoveryServiceTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private readonly PluginDiscoveryService _sut = new();

    // ── Collector discovery ──────────────────────────────────────────────

    [Fact]
    public async Task DiscoverCollectors_FindsAllEnabled()
    {
        string collectorsDir = CreateCollectorFixtures("alpha", "beta", "gamma");
        DiagnosticsConfig config = new();

        IReadOnlyList<DiscoveredCollector> result =
            await _sut.DiscoverCollectorsAsync(collectorsDir, config);

        _ = result.Should().HaveCount(3);
        _ = result.Select(c => c.Name).Should().BeEquivalentTo("alpha", "beta", "gamma");
    }

    [Fact]
    public async Task DiscoverCollectors_DisabledPlugin_Excluded()
    {
        string collectorsDir = CreateCollectorFixtures("enabled-one", "disabled-one");
        DiagnosticsConfig config = new(
            CollectorSettings: new Dictionary<string, CollectorSettingsEntry>(StringComparer.Ordinal)
            {
                ["enabled-one"] = new(Enabled: true),
                ["disabled-one"] = new(Enabled: false),
            });

        IReadOnlyList<DiscoveredCollector> result =
            await _sut.DiscoverCollectorsAsync(collectorsDir, config);

        _ = result.Should().ContainSingle()
            .Which.Name.Should().Be("enabled-one");
    }

    [Fact]
    public async Task DiscoverCollectors_GroupAssignment_Correct()
    {
        string collectorsDir = CreateCollectorFixturesWithGroups(
            ("cpu-collector", "etw-cpu"),
            ("gc-collector", "etw-gc"),
            ("basic-collector", null));
        DiagnosticsConfig config = new();

        IReadOnlyList<DiscoveredCollector> result =
            await _sut.DiscoverCollectorsAsync(collectorsDir, config);

        _ = result.Should().HaveCount(3);
        _ = result.Single(c => string.Equals(c.Name, "cpu-collector", StringComparison.Ordinal))
            .Group.Should().Be("etw-cpu");
        _ = result.Single(c => string.Equals(c.Name, "gc-collector", StringComparison.Ordinal))
            .Group.Should().Be("etw-gc");
        _ = result.Single(c => string.Equals(c.Name, "basic-collector", StringComparison.Ordinal))
            .Group.Should().Be("default");
    }

    [Fact]
    public async Task DiscoverCollectors_MergesSettings_ConfigOverridesDefaults()
    {
        string collectorsDir = CreateCollectorFixtureWithSettings(
            "my-collector",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["MaxCollectSec"] = 60,
                ["BufferSizeMB"] = 128,
            });

        DiagnosticsConfig config = new(
            CollectorSettings: new Dictionary<string, CollectorSettingsEntry>(StringComparer.Ordinal)
            {
                ["my-collector"] = new(MaxCollectSec: 200, BufferSizeMB: 512),
            });

        IReadOnlyList<DiscoveredCollector> result =
            await _sut.DiscoverCollectorsAsync(collectorsDir, config);

        DiscoveredCollector collector = result.Should().ContainSingle().Subject;
        _ = collector.MergedSettings.MaxCollectSec.Should().Be(200);
        _ = collector.MergedSettings.BufferSizeMB.Should().Be(512);
    }

    [Fact]
    public async Task DiscoverCollectors_InjectsPerfViewExePath()
    {
        string collectorsDir = CreateCollectorFixtures("perfview-cpu");
        DiagnosticsConfig config = new(PerfViewExePath: @"C:\tools\PerfView.exe");

        IReadOnlyList<DiscoveredCollector> result =
            await _sut.DiscoverCollectorsAsync(collectorsDir, config);

        DiscoveredCollector collector = result.Should().ContainSingle().Subject;
        _ = collector.MergedSettings.PerfViewExePath.Should().Be(@"C:\tools\PerfView.exe");
    }

    // ── Analyzer discovery ───────────────────────────────────────────────

    [Fact]
    public async Task DiscoverAnalyzers_FindsAllEnabled()
    {
        string analyzersDir = CreateAnalyzerFixtures("cpu-hotspots", "memory-gc");
        DiagnosticsConfig config = new();

        IReadOnlyList<DiscoveredAnalyzer> result =
            await _sut.DiscoverAnalyzersAsync(analyzersDir, config);

        _ = result.Should().HaveCount(2);
        _ = result.Select(a => a.Name).Should().BeEquivalentTo("cpu-hotspots", "memory-gc");
    }

    [Fact]
    public async Task DiscoverAnalyzers_DisabledPlugin_Excluded()
    {
        string analyzersDir = CreateAnalyzerFixtures("active", "inactive");
        DiagnosticsConfig config = new(
            AnalyzerSettings: new Dictionary<string, AnalyzerSettingsEntry>(StringComparer.Ordinal)
            {
                ["active"] = new(Enabled: true),
                ["inactive"] = new(Enabled: false),
            });

        IReadOnlyList<DiscoveredAnalyzer> result =
            await _sut.DiscoverAnalyzersAsync(analyzersDir, config);

        _ = result.Should().ContainSingle()
            .Which.Name.Should().Be("active");
    }

    [Fact]
    public async Task DiscoverAnalyzers_RequiredCollectors_Populated()
    {
        string analyzersDir = CreateAnalyzerFixtureWithRequiredCollectors(
            "cpu-hotspots", ["perfview-cpu", "dotnet-counters"]);
        DiagnosticsConfig config = new();

        IReadOnlyList<DiscoveredAnalyzer> result =
            await _sut.DiscoverAnalyzersAsync(analyzersDir, config);

        DiscoveredAnalyzer analyzer = result.Should().ContainSingle().Subject;
        _ = analyzer.Metadata.RequiredCollectors.Should().BeEquivalentTo("perfview-cpu", "dotnet-counters");
    }

    // ── Fixture helpers ──────────────────────────────────────────────────

    private string CreateCollectorFixtures(params string[] names)
    {
        string root = Path.Combine(TempDir, "collectors");
        Directory.CreateDirectory(root);

        foreach (string name in names)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
#pragma warning disable RS0030 // Sync I/O is intentional in test setup
            File.WriteAllText(
                Path.Combine(dir, "collector.yaml"),
                $"Name: {name}\nDescription: Test collector {name}\n");
#pragma warning restore RS0030
        }

        return root;
    }

    private string CreateCollectorFixturesWithGroups(params (string Name, string? Group)[] items)
    {
        string root = Path.Combine(TempDir, "collectors");
        Directory.CreateDirectory(root);

        foreach ((string name, string? group) in items)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            string yaml = $"Name: {name}\n";
            if (group is not null)
            {
                yaml += $"Group: {group}\n";
            }

#pragma warning disable RS0030 // Sync I/O is intentional in test setup
            File.WriteAllText(Path.Combine(dir, "collector.yaml"), yaml);
#pragma warning restore RS0030
        }

        return root;
    }

    private string CreateCollectorFixtureWithSettings(
        string name,
        Dictionary<string, object?> defaults)
    {
        string root = Path.Combine(TempDir, "collectors");
        string dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);

        string settingsYaml = string.Join(
            "\n",
            defaults.Select(kv => $"  {kv.Key}: {kv.Value}"));
        string yaml = $"Name: {name}\nDefaultSettings:\n{settingsYaml}\n";
#pragma warning disable RS0030 // Sync I/O is intentional in test setup
        File.WriteAllText(Path.Combine(dir, "collector.yaml"), yaml);
#pragma warning restore RS0030

        return root;
    }

    private string CreateAnalyzerFixtures(params string[] names)
    {
        string root = Path.Combine(TempDir, "analyzers");
        Directory.CreateDirectory(root);

        foreach (string name in names)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
#pragma warning disable RS0030 // Sync I/O is intentional in test setup
            File.WriteAllText(
                Path.Combine(dir, "analyzer.yaml"),
                $"Name: {name}\nDescription: Test analyzer {name}\n");
#pragma warning restore RS0030
        }

        return root;
    }

    private string CreateAnalyzerFixtureWithRequiredCollectors(
        string name,
        string[] requiredCollectors)
    {
        string root = Path.Combine(TempDir, "analyzers");
        string dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);

        string collectorsYaml = string.Join(
            "\n",
            requiredCollectors.Select(c => $"  - {c}"));
        string yaml = $"Name: {name}\nRequiredCollectors:\n{collectorsYaml}\n";
#pragma warning disable RS0030 // Sync I/O is intentional in test setup
        File.WriteAllText(Path.Combine(dir, "analyzer.yaml"), yaml);
#pragma warning restore RS0030

        return root;
    }
}
