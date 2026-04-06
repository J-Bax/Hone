using FluentAssertions;
using Hone.Core.Config;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Config;

public sealed class ConfigMergerTests(ITestOutputHelper output) : HoneTestBase(output)
{
    // ── Shared engine and target configs ─────────────────────────────────────

    private static HoneConfig CreateEngineConfig() => new(
        Api: new ApiConfig(
            SolutionPath: "engine/Api.sln",
            ProjectPath: "engine/Api",
            BaseUrl: "http://engine:8080",
            StartupTimeout: 120,
            ResultsPath: "engine/.hone/results",
            MetadataPath: "engine/.hone/results/metadata"),
        Tolerances: new TolerancesConfig(
            MaxRegressionPct: 0.15,
            MinAbsoluteP95DeltaMs: 10,
            StaleExperimentsBeforeStop: 3,
            Efficiency: new EfficiencyConfig(
                Enabled: true,
                MinCpuReductionPct: 0.08)),
        ScaleTest: new ScaleTestConfig(
            ScenarioPath: "engine/baseline.js",
            MeasuredRuns: 7),
        Loop: new LoopConfig(
            MaxExperiments: 100,
            BranchPrefix: "engine/exp",
            StackedDiffs: true),
        Agents: new AgentConfig(
            DefaultModel: "engine-model",
            AgentTimeoutSec: 1200),
        Diagnostics: new DiagnosticsConfig(
            Enabled: true,
            CollectorsPath: "engine/collectors"),
        Logging: new LoggingConfig(
            Level: "debug",
            MaxFileSizeMB: 200),
        Implementer: new ImplementerConfig(
            MaxAttempts: 4,
            MaxDiffGrowthFactor: 2.5),
        DotnetCounters: new DotnetCountersConfig(
            Enabled: true,
            RefreshIntervalSeconds: 2));

    // ── ConfigMerger_TargetOverridesEngine_SectionLevel ─────────────────────

    [Fact]
    public void Merge_TargetOverridesEngine_SectionLevel()
    {
        HoneConfig engine = CreateEngineConfig();
        var target = new HoneConfig(
            Api: new ApiConfig(BaseUrl: "http://target:5050"));

        HoneConfig merged = ConfigMerger.Merge(engine, target);

        // Target Api.BaseUrl should override engine
        _ = merged.Api.BaseUrl.Should().Be("http://target:5050");

        // Engine Tolerances should be preserved (target didn't override)
        _ = merged.Tolerances.MaxRegressionPct.Should().Be(0.15);
        _ = merged.Tolerances.MinAbsoluteP95DeltaMs.Should().Be(10);
        _ = merged.Tolerances.Efficiency.Enabled.Should().BeTrue();
        _ = merged.Tolerances.Efficiency.MinCpuReductionPct.Should().Be(0.08);

        // Other engine sections should be preserved
        _ = merged.Loop.MaxExperiments.Should().Be(100);
        _ = merged.Agents.DefaultModel.Should().Be("engine-model");
        _ = merged.Logging.Level.Should().Be("debug");
    }

    // ── ConfigMerger_CliOverridesEverything ──────────────────────────────────

    [Fact]
    public void Merge_CliOverridesEverything()
    {
        HoneConfig engine = CreateEngineConfig();
        var target = new HoneConfig(
            Loop: new LoopConfig(MaxExperiments: 20));
        var cli = new CliOverrides(
            MaxExperiments: 5,
            Model: "cli-model",
            StackedDiffs: false,
            WaitForMerge: true,
            SkipClassification: true,
            DiagnosticsEnabled: false);

        HoneConfig merged = ConfigMerger.Merge(engine, target, cli);

        // CLI overrides should win over both engine and target
        _ = merged.Loop.MaxExperiments.Should().Be(5);
        _ = merged.Loop.StackedDiffs.Should().BeFalse();
        _ = merged.Loop.WaitForMerge.Should().BeTrue();
        _ = merged.Loop.SkipClassification.Should().BeTrue();
        _ = merged.Agents.DefaultModel.Should().Be("cli-model");
        _ = merged.Diagnostics.Enabled.Should().BeFalse();
    }

    // ── ConfigMerger_PartialTargetOverride_MergesCorrectly ──────────────────

    [Fact]
    public void Merge_PartialTargetOverride_MergesCorrectly()
    {
        HoneConfig engine = CreateEngineConfig();

        // Target only overrides Api.BaseUrl — other Api properties should come from engine
        var target = new HoneConfig(
            Api: new ApiConfig(BaseUrl: "http://target:5050"));

        HoneConfig merged = ConfigMerger.Merge(engine, target);

        // Overridden value
        _ = merged.Api.BaseUrl.Should().Be("http://target:5050");

        // Engine Api properties preserved
        _ = merged.Api.SolutionPath.Should().Be("engine/Api.sln");
        _ = merged.Api.ProjectPath.Should().Be("engine/Api");
        _ = merged.Api.StartupTimeout.Should().Be(120);
        _ = merged.Api.ResultsPath.Should().Be("engine/.hone/results");
        _ = merged.Api.MetadataPath.Should().Be("engine/.hone/results/metadata");

        // Other engine sections untouched
        _ = merged.ScaleTest.ScenarioPath.Should().Be("engine/baseline.js");
        _ = merged.ScaleTest.MeasuredRuns.Should().Be(7);
        _ = merged.Implementer.MaxAttempts.Should().Be(4);
    }

    // ── Multiple sections with partial overrides ────────────────────────────

    [Fact]
    public void Merge_MultipleSectionsPartialOverride_MergesCorrectly()
    {
        HoneConfig engine = CreateEngineConfig();
        var target = new HoneConfig(
            Api: new ApiConfig(BaseUrl: "http://target:5050"),
            Loop: new LoopConfig(MaxExperiments: 25),
            Logging: new LoggingConfig(Level: "warning"));

        HoneConfig merged = ConfigMerger.Merge(engine, target);

        // Target overrides
        _ = merged.Api.BaseUrl.Should().Be("http://target:5050");
        _ = merged.Loop.MaxExperiments.Should().Be(25);
        _ = merged.Logging.Level.Should().Be("warning");

        // Engine values preserved within partially-overridden sections
        _ = merged.Api.SolutionPath.Should().Be("engine/Api.sln");
        _ = merged.Loop.BranchPrefix.Should().Be("engine/exp");
        _ = merged.Loop.StackedDiffs.Should().BeTrue();
        _ = merged.Logging.MaxFileSizeMB.Should().Be(200);

        // Untouched engine sections
        _ = merged.Agents.DefaultModel.Should().Be("engine-model");
        _ = merged.Implementer.MaxAttempts.Should().Be(4);
    }

    // ── CLI with null overrides doesn't change merged config ────────────────

    [Fact]
    public void Merge_NullCliOverrides_DoesNotAlterMerged()
    {
        HoneConfig engine = CreateEngineConfig();
        var target = new HoneConfig();

        HoneConfig withNull = ConfigMerger.Merge(engine, target, cli: null);
        HoneConfig withoutCli = ConfigMerger.Merge(engine, target);

        _ = withNull.Loop.MaxExperiments.Should().Be(withoutCli.Loop.MaxExperiments);
        _ = withNull.Agents.DefaultModel.Should().Be(withoutCli.Agents.DefaultModel);
        _ = withNull.Diagnostics.Enabled.Should().Be(withoutCli.Diagnostics.Enabled);
    }

    // ── CLI with empty overrides preserves merged values ────────────────────

    [Fact]
    public void Merge_EmptyCliOverrides_PreservesMergedValues()
    {
        HoneConfig engine = CreateEngineConfig();
        var target = new HoneConfig(
            Loop: new LoopConfig(MaxExperiments: 20));
        var cli = new CliOverrides();

        HoneConfig merged = ConfigMerger.Merge(engine, target, cli);

        // Target override should be preserved since CLI has no overrides
        _ = merged.Loop.MaxExperiments.Should().Be(20);
        _ = merged.Agents.DefaultModel.Should().Be("engine-model");
        _ = merged.Diagnostics.Enabled.Should().BeTrue();
    }

    // ── Both engine and target at defaults → still returns valid config ─────

    [Fact]
    public void Merge_BothDefaults_ReturnsValidConfig()
    {
        var engine = new HoneConfig();
        var target = new HoneConfig();

        HoneConfig merged = ConfigMerger.Merge(engine, target);

        _ = merged.Should().NotBeNull();
        _ = merged.Api.SolutionPath.Should().Be("sample-api/SampleApi.sln");
        _ = merged.Loop.MaxExperiments.Should().Be(999);
        _ = merged.Agents.DefaultModel.Should().Be("claude-sonnet-4.5");
    }
}
