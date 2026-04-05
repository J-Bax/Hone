using System.Text.Json;
using FluentAssertions;
using Hone.Core.Config;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Config;

public sealed class HoneConfigTests(ITestOutputHelper output) : HoneTestBase(output)
{
    // ── ApiConfig defaults ──────────────────────────────────────────────────

    [Fact]
    public void ApiConfig_DefaultValues_MatchConfigPsd1()
    {
        var api = new ApiConfig();

        _ = api.SolutionPath.Should().Be("sample-api/SampleApi.sln");
        _ = api.ProjectPath.Should().Be("sample-api/SampleApi");
        _ = api.SourceCodePaths.Should().BeEquivalentTo(["Controllers", "Data", "Models", "Pages"]);
        _ = api.SourceFileGlob.Should().Be("*.cs");
        _ = api.TestProjectPath.Should().Be("sample-api/SampleApi.Tests");
        _ = api.BaseUrl.Should().Be("http://localhost:0");
        _ = api.HealthEndpoint.Should().Be("/health");
        _ = api.GcEndpoint.Should().Be("/diag/gc");
        _ = api.StartupTimeout.Should().Be(90);
        _ = api.ResultsPath.Should().Be("sample-api/.hone/results");
        _ = api.MetadataPath.Should().Be("sample-api/.hone/results/metadata");
    }

    // ── TolerancesConfig defaults ───────────────────────────────────────────

    [Fact]
    public void TolerancesConfig_DefaultValues_MatchConfigPsd1()
    {
        var tolerances = new TolerancesConfig();

        _ = tolerances.MaxRegressionPct.Should().Be(0.10);
        _ = tolerances.MinAbsoluteP95DeltaMs.Should().Be(5);
        _ = tolerances.MinAbsoluteRPSDelta.Should().Be(5);
        _ = tolerances.MinAbsoluteErrorRateDelta.Should().Be(0.005);
        _ = tolerances.MinImprovementPct.Should().Be(0);
        _ = tolerances.StaleExperimentsBeforeStop.Should().Be(2);
        _ = tolerances.MaxConsecutiveFailures.Should().Be(10);

        // Efficiency sub-section
        _ = tolerances.Efficiency.Should().NotBeNull();
        _ = tolerances.Efficiency.Enabled.Should().BeTrue();
        _ = tolerances.Efficiency.MinCpuReductionPct.Should().Be(0.05);
        _ = tolerances.Efficiency.MinWorkingSetReductionPct.Should().Be(0.05);
    }

    // ── ScaleTestConfig defaults ────────────────────────────────────────────

    [Fact]
    public void ScaleTestConfig_DefaultValues_MatchConfigPsd1()
    {
        var scaleTest = new ScaleTestConfig();

        _ = scaleTest.ScenarioPath.Should().Be("sample-api/scale-tests/scenarios/baseline.js");
        _ = scaleTest.ScenarioRegistryPath.Should().Be("sample-api/scale-tests/thresholds.json");
        _ = scaleTest.ExtraArgs.Should().BeEmpty();
        _ = scaleTest.WarmupEnabled.Should().BeTrue();
        _ = scaleTest.WarmupScenarioPath.Should().Be("sample-api/scale-tests/scenarios/warmup.js");
        _ = scaleTest.MeasuredRuns.Should().Be(5);
        _ = scaleTest.CooldownSeconds.Should().Be(3);
    }

    // ── LoopConfig defaults ─────────────────────────────────────────────────

    [Fact]
    public void LoopConfig_DefaultValues_MatchConfigPsd1()
    {
        var loop = new LoopConfig();

        _ = loop.MaxExperiments.Should().Be(999);
        _ = loop.BranchPrefix.Should().Be("hone/experiment");
        _ = loop.StackedDiffs.Should().BeTrue();
        _ = loop.WaitForMerge.Should().BeFalse();
        _ = loop.SkipClassification.Should().BeFalse();
    }

    // ── AgentConfig defaults ────────────────────────────────────────────────

    [Fact]
    public void AgentConfig_DefaultValues_MatchConfigPsd1()
    {
        var agents = new AgentConfig();

        _ = agents.DefaultModel.Should().Be("claude-sonnet-4.5");
        _ = agents.AnalysisModel.Should().Be("claude-opus-4.6");
        _ = agents.ClassificationModel.Should().Be("claude-opus-4.6");
        _ = agents.ImplementerModel.Should().Be("claude-sonnet-4.6");
        _ = agents.AgentTimeoutSec.Should().Be(1800);
    }

    // ── DiagnosticsConfig defaults ──────────────────────────────────────────

    [Fact]
    public void DiagnosticsConfig_DefaultValues_MatchConfigPsd1()
    {
        var diagnostics = new DiagnosticsConfig();

        _ = diagnostics.Enabled.Should().BeTrue();
        _ = diagnostics.CollectorsPath.Should().Be("harness/collectors");
        _ = diagnostics.AnalyzersPath.Should().Be("harness/analyzers");
        _ = diagnostics.PerfViewExePath.Should().Be("tools/PerfView/PerfView.exe");
        _ = diagnostics.DiagnosticScenarioPath.Should().BeNull();
        _ = diagnostics.DiagnosticRuns.Should().Be(1);
        _ = diagnostics.K6TimeoutSec.Should().Be(300);

        // Collector settings
        _ = diagnostics.CollectorSettings.Should().ContainKey("perfview-cpu");
        _ = diagnostics.CollectorSettings["perfview-cpu"].Enabled.Should().BeTrue();
        _ = diagnostics.CollectorSettings["perfview-cpu"].MaxCollectSec.Should().Be(150);
        _ = diagnostics.CollectorSettings["perfview-cpu"].StopTimeoutSec.Should().Be(600);
        _ = diagnostics.CollectorSettings["perfview-cpu"].ExportTimeoutSec.Should().Be(600);
        _ = diagnostics.CollectorSettings["perfview-cpu"].BufferSizeMB.Should().Be(256);
        _ = diagnostics.CollectorSettings["perfview-cpu"].MaxStacks.Should().Be(100);

        _ = diagnostics.CollectorSettings.Should().ContainKey("perfview-gc");
        _ = diagnostics.CollectorSettings["perfview-gc"].Enabled.Should().BeTrue();
        _ = diagnostics.CollectorSettings["perfview-gc"].MaxStacks.Should().BeNull();

        _ = diagnostics.CollectorSettings.Should().ContainKey("dotnet-counters");
        _ = diagnostics.CollectorSettings["dotnet-counters"].Enabled.Should().BeTrue();

        // Analyzer settings
        _ = diagnostics.AnalyzerSettings.Should().ContainKey("cpu-hotspots");
        _ = diagnostics.AnalyzerSettings["cpu-hotspots"].Enabled.Should().BeTrue();
        _ = diagnostics.AnalyzerSettings["cpu-hotspots"].Model.Should().Be("claude-opus-4.6");
        _ = diagnostics.AnalyzerSettings["cpu-hotspots"].MaxStacks.Should().Be(100);

        _ = diagnostics.AnalyzerSettings.Should().ContainKey("memory-gc");
        _ = diagnostics.AnalyzerSettings["memory-gc"].Enabled.Should().BeTrue();
        _ = diagnostics.AnalyzerSettings["memory-gc"].Model.Should().Be("claude-opus-4.6");
    }

    // ── ImplementerConfig defaults ──────────────────────────────────────────

    [Fact]
    public void ImplementerConfig_DefaultValues_MatchConfigPsd1()
    {
        var implementer = new ImplementerConfig();

        _ = implementer.MaxAttempts.Should().Be(3);
        _ = implementer.MaxDiffGrowthFactor.Should().Be(3.0);
        _ = implementer.TestFileGuard.Should().BeTrue();
    }

    // ── LoggingConfig defaults ──────────────────────────────────────────────

    [Fact]
    public void LoggingConfig_DefaultValues_MatchConfigPsd1()
    {
        var logging = new LoggingConfig();

        _ = logging.Level.Should().Be("info");
        _ = logging.MaxFileSizeMB.Should().Be(50);
    }

    // ── DotnetCountersConfig defaults ───────────────────────────────────────

    [Fact]
    public void DotnetCountersConfig_DefaultValues_MatchConfigPsd1()
    {
        var counters = new DotnetCountersConfig();

        _ = counters.Enabled.Should().BeTrue();
        _ = counters.Providers.Should().BeEquivalentTo(
        [
            "System.Runtime",
            "Microsoft.AspNetCore.Hosting",
            "Microsoft.AspNetCore.Http.Connections",
            "System.Net.Http",
        ]);
        _ = counters.RefreshIntervalSeconds.Should().Be(1);
    }

    // ── CliOverrides defaults ───────────────────────────────────────────────

    [Fact]
    public void CliOverrides_AllNull_ByDefault()
    {
        var overrides = new CliOverrides();

        _ = overrides.MaxExperiments.Should().BeNull();
        _ = overrides.Model.Should().BeNull();
        _ = overrides.StackedDiffs.Should().BeNull();
        _ = overrides.WaitForMerge.Should().BeNull();
        _ = overrides.SkipClassification.Should().BeNull();
        _ = overrides.DiagnosticsEnabled.Should().BeNull();
    }

    // ── HoneConfig aggregate defaults ───────────────────────────────────────

    [Fact]
    public void HoneConfig_DefaultValues_MatchExpected()
    {
        var config = new HoneConfig();

        // All sections should be non-null with their own defaults
        _ = config.Api.Should().NotBeNull();
        _ = config.Tolerances.Should().NotBeNull();
        _ = config.ScaleTest.Should().NotBeNull();
        _ = config.Loop.Should().NotBeNull();
        _ = config.Agents.Should().NotBeNull();
        _ = config.Diagnostics.Should().NotBeNull();
        _ = config.Logging.Should().NotBeNull();
        _ = config.Implementer.Should().NotBeNull();
        _ = config.DotnetCounters.Should().NotBeNull();

        // Spot-check key defaults through the root
        _ = config.Api.SolutionPath.Should().Be("sample-api/SampleApi.sln");
        _ = config.Api.BaseUrl.Should().Be("http://localhost:0");
        _ = config.Api.StartupTimeout.Should().Be(90);
        _ = config.Tolerances.MaxRegressionPct.Should().Be(0.10);
        _ = config.Tolerances.Efficiency.Enabled.Should().BeTrue();
        _ = config.ScaleTest.MeasuredRuns.Should().Be(5);
        _ = config.Loop.MaxExperiments.Should().Be(999);
        _ = config.Loop.StackedDiffs.Should().BeTrue();
        _ = config.Agents.DefaultModel.Should().Be("claude-sonnet-4.5");
        _ = config.Implementer.MaxAttempts.Should().Be(3);
        _ = config.Logging.Level.Should().Be("info");
        _ = config.Diagnostics.Enabled.Should().BeTrue();
        _ = config.DotnetCounters.Enabled.Should().BeTrue();
    }

    // ── Serialization round-trip ────────────────────────────────────────────

    [Fact]
    public void HoneConfig_RoundTrips_ThroughJson()
    {
        var original = new HoneConfig(
            Api: new ApiConfig(
                SolutionPath: "custom/Api.sln",
                ProjectPath: "custom/Api",
                SourceCodePaths: ["Src"],
                BaseUrl: "http://localhost:5000"),
            Tolerances: new TolerancesConfig(
                MaxRegressionPct: 0.15,
                Efficiency: new EfficiencyConfig(Enabled: false)),
            ScaleTest: new ScaleTestConfig(
                ScenarioPath: "tests/load.js",
                MeasuredRuns: 3,
                ExtraArgs: ["--vus", "10"]),
            Loop: new LoopConfig(MaxExperiments: 50, StackedDiffs: false),
            Agents: new AgentConfig(DefaultModel: "gpt-4o"),
            Diagnostics: new DiagnosticsConfig(
                Enabled: false,
                CollectorSettings: new Dictionary<string, CollectorSettingsEntry>(StringComparer.Ordinal)
                {
                    ["custom"] = new(Enabled: true, MaxCollectSec: 60),
                },
                AnalyzerSettings: new Dictionary<string, AnalyzerSettingsEntry>(StringComparer.Ordinal)
                {
                    ["custom"] = new(Enabled: true, Model: "gpt-4o"),
                }),
            Logging: new LoggingConfig(Level: "verbose", MaxFileSizeMB: 100),
            Implementer: new ImplementerConfig(MaxAttempts: 5),
            DotnetCounters: new DotnetCountersConfig(
                Enabled: false,
                Providers: ["System.Runtime"]));

        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(original, options);
        Output.WriteLine(json);

        HoneConfig? deserialized = JsonSerializer.Deserialize<HoneConfig>(json, options);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Api.SolutionPath.Should().Be("custom/Api.sln");
        _ = deserialized.Api.SourceCodePaths.Should().BeEquivalentTo(["Src"]);
        _ = deserialized.Tolerances.MaxRegressionPct.Should().Be(0.15);
        _ = deserialized.Tolerances.Efficiency.Enabled.Should().BeFalse();
        _ = deserialized.ScaleTest.MeasuredRuns.Should().Be(3);
        _ = deserialized.ScaleTest.ExtraArgs.Should().BeEquivalentTo(["--vus", "10"]);
        _ = deserialized.Loop.MaxExperiments.Should().Be(50);
        _ = deserialized.Loop.StackedDiffs.Should().BeFalse();
        _ = deserialized.Agents.DefaultModel.Should().Be("gpt-4o");
        _ = deserialized.Diagnostics.Enabled.Should().BeFalse();
        _ = deserialized.Diagnostics.CollectorSettings.Should().ContainKey("custom");
        _ = deserialized.Diagnostics.AnalyzerSettings.Should().ContainKey("custom");
        _ = deserialized.Logging.Level.Should().Be("verbose");
        _ = deserialized.Logging.MaxFileSizeMB.Should().Be(100);
        _ = deserialized.Implementer.MaxAttempts.Should().Be(5);
        _ = deserialized.DotnetCounters.Enabled.Should().BeFalse();
        _ = deserialized.DotnetCounters.Providers.Should().BeEquivalentTo(["System.Runtime"]);
    }
}
