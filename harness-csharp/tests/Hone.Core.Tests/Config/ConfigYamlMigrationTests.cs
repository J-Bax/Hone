using FluentAssertions;
using Hone.Core.Config;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Config;

/// <summary>
/// Validates that the migrated YAML config files load correctly and match
/// the expected values from the original config sources.
/// </summary>
public sealed class ConfigYamlMigrationTests(ITestOutputHelper output) : HoneTestBase(output)
{
    // ── Path resolution ──────────────────────────────────────────────────────

    /// <summary>
    /// Walks up from the test assembly directory to find the harness-csharp root
    /// (the directory containing Hone.slnx).
    /// </summary>
    private static string FindHarnessCSharpRoot()
    {
        string? dir = AppContext.BaseDirectory;

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Hone.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            "Cannot find harness-csharp root directory (Hone.slnx not found). " +
            $"Searched from: {AppContext.BaseDirectory}");
    }

    private static string RepoRoot => Path.GetDirectoryName(FindHarnessCSharpRoot())!;

    // ── Engine defaults (harness-csharp/config.yaml) ────────────────────────

    [Fact]
    public void EngineDefaults_Api_MatchesPsd1Values()
    {
        string path = Path.Combine(FindHarnessCSharpRoot(), "config.yaml");

        HoneConfig config = ConfigLoader.Load(path);

        _ = config.Api.SolutionPath.Should().Be("sample-api/SampleApi.sln");
        _ = config.Api.ProjectPath.Should().Be("sample-api/SampleApi");
        _ = config.Api.TestProjectPath.Should().Be("sample-api/SampleApi.Tests");
        _ = config.Api.SourceFileGlob.Should().Be("*.cs");
        _ = config.Api.SourceCodePaths.Should().BeEquivalentTo(["Controllers", "Data", "Models", "Pages"]);
        _ = config.Api.BaseUrl.Should().Be("http://localhost:0");
        _ = config.Api.HealthEndpoint.Should().Be("/health");
        _ = config.Api.GcEndpoint.Should().Be("/diag/gc");
        _ = config.Api.StartupTimeout.Should().Be(90);
        _ = config.Api.ResultsPath.Should().Be(@"sample-api/.hone/results");
        _ = config.Api.MetadataPath.Should().Be(@"sample-api/.hone/results/metadata");
    }

    [Fact]
    public void EngineDefaults_Tolerances_MatchesPsd1Values()
    {
        string path = Path.Combine(FindHarnessCSharpRoot(), "config.yaml");

        HoneConfig config = ConfigLoader.Load(path);

        _ = config.Tolerances.MaxRegressionPct.Should().Be(0.10);
        _ = config.Tolerances.MinAbsoluteP95DeltaMs.Should().Be(5);
        _ = config.Tolerances.MinAbsoluteRPSDelta.Should().Be(5);
        _ = config.Tolerances.MinAbsoluteErrorRateDelta.Should().Be(0.005);
        _ = config.Tolerances.MinImprovementPct.Should().Be(0);
        _ = config.Tolerances.StaleExperimentsBeforeStop.Should().Be(2);
        _ = config.Tolerances.MaxConsecutiveFailures.Should().Be(10);
        _ = config.Tolerances.Efficiency.Enabled.Should().BeTrue();
        _ = config.Tolerances.Efficiency.MinCpuReductionPct.Should().Be(0.05);
        _ = config.Tolerances.Efficiency.MinWorkingSetReductionPct.Should().Be(0.05);
    }

    [Fact]
    public void EngineDefaults_ScaleTest_MatchesPsd1Values()
    {
        string path = Path.Combine(FindHarnessCSharpRoot(), "config.yaml");

        HoneConfig config = ConfigLoader.Load(path);

        _ = config.ScaleTest.ScenarioPath.Should().Be("sample-api/scale-tests/scenarios/baseline.js");
        _ = config.ScaleTest.ScenarioRegistryPath.Should().Be("sample-api/scale-tests/thresholds.json");
        _ = config.ScaleTest.ExtraArgs.Should().BeEmpty();
        _ = config.ScaleTest.WarmupEnabled.Should().BeTrue();
        _ = config.ScaleTest.WarmupScenarioPath.Should().Be("sample-api/scale-tests/scenarios/warmup.js");
        _ = config.ScaleTest.MeasuredRuns.Should().Be(5);
        _ = config.ScaleTest.CooldownSeconds.Should().Be(3);
    }

    [Fact]
    public void EngineDefaults_Loop_MatchesPsd1Values()
    {
        string path = Path.Combine(FindHarnessCSharpRoot(), "config.yaml");

        HoneConfig config = ConfigLoader.Load(path);

        _ = config.Loop.MaxExperiments.Should().Be(999);
        _ = config.Loop.BranchPrefix.Should().Be("hone/experiment");
        _ = config.Loop.StackedDiffs.Should().BeTrue();
        _ = config.Loop.WaitForMerge.Should().BeFalse();
        _ = config.Loop.SkipClassification.Should().BeFalse();
    }

    [Fact]
    public void EngineDefaults_Agents_MatchesPsd1CopilotSection()
    {
        string path = Path.Combine(FindHarnessCSharpRoot(), "config.yaml");

        HoneConfig config = ConfigLoader.Load(path);

        // Agents.DefaultModel (was Copilot.Model in original config)
        _ = config.Agents.DefaultModel.Should().Be("claude-sonnet-4.5");
        _ = config.Agents.AnalysisModel.Should().Be("claude-opus-4.6");
        _ = config.Agents.ClassificationModel.Should().Be("claude-opus-4.6");
        _ = config.Agents.ImplementerModel.Should().Be("claude-sonnet-4.6");
        _ = config.Agents.AgentTimeoutSec.Should().Be(1800);
    }

    [Fact]
    public void EngineDefaults_Implementer_MatchesPsd1FixerSection()
    {
        string path = Path.Combine(FindHarnessCSharpRoot(), "config.yaml");

        HoneConfig config = ConfigLoader.Load(path);

        // Implementer section (was Fixer in original config)
        _ = config.Implementer.MaxAttempts.Should().Be(3);
        _ = config.Implementer.MaxDiffGrowthFactor.Should().Be(3.0);
        _ = config.Implementer.TestFileGuard.Should().BeTrue();
    }

    [Fact]
    public void EngineDefaults_Logging_MatchesPsd1Values()
    {
        string path = Path.Combine(FindHarnessCSharpRoot(), "config.yaml");

        HoneConfig config = ConfigLoader.Load(path);

        _ = config.Logging.Level.Should().Be("info");
        _ = config.Logging.MaxFileSizeMB.Should().Be(50);
    }

    [Fact]
    public void EngineDefaults_DotnetCounters_MatchesPsd1Values()
    {
        string path = Path.Combine(FindHarnessCSharpRoot(), "config.yaml");

        HoneConfig config = ConfigLoader.Load(path);

        _ = config.DotnetCounters.Enabled.Should().BeTrue();
        _ = config.DotnetCounters.RefreshIntervalSeconds.Should().Be(1);
        _ = config.DotnetCounters.Providers.Should().BeEquivalentTo(
        [
            "System.Runtime",
            "Microsoft.AspNetCore.Hosting",
            "Microsoft.AspNetCore.Http.Connections",
            "System.Net.Http",
        ]);
    }

    [Fact]
    public void EngineDefaults_Diagnostics_MatchesPsd1Values()
    {
        string path = Path.Combine(FindHarnessCSharpRoot(), "config.yaml");

        HoneConfig config = ConfigLoader.Load(path);

        _ = config.Diagnostics.Enabled.Should().BeTrue();
        _ = config.Diagnostics.CollectorsPath.Should().Be("harness-legacy/collectors");
        _ = config.Diagnostics.AnalyzersPath.Should().Be("harness-legacy/analyzers");
        _ = config.Diagnostics.PerfViewExePath.Should().Be("tools/PerfView/PerfView.exe");
        _ = config.Diagnostics.DiagnosticScenarioPath.Should().BeNull();
        _ = config.Diagnostics.DiagnosticRuns.Should().Be(1);
        _ = config.Diagnostics.K6TimeoutSec.Should().Be(300);

        // CollectorSettings
        _ = config.Diagnostics.CollectorSettings.Should().ContainKey("perfview-cpu");
        CollectorSettingsEntry cpu = config.Diagnostics.CollectorSettings["perfview-cpu"];
        _ = cpu.Enabled.Should().BeTrue();
        _ = cpu.MaxCollectSec.Should().Be(150);
        _ = cpu.StopTimeoutSec.Should().Be(600);
        _ = cpu.ExportTimeoutSec.Should().Be(600);
        _ = cpu.BufferSizeMB.Should().Be(256);
        _ = cpu.MaxStacks.Should().Be(100);

        _ = config.Diagnostics.CollectorSettings.Should().ContainKey("perfview-gc");
        CollectorSettingsEntry gc = config.Diagnostics.CollectorSettings["perfview-gc"];
        _ = gc.Enabled.Should().BeTrue();
        _ = gc.MaxCollectSec.Should().Be(150);
        _ = gc.StopTimeoutSec.Should().Be(600);
        _ = gc.ExportTimeoutSec.Should().Be(600);
        _ = gc.BufferSizeMB.Should().Be(256);

        _ = config.Diagnostics.CollectorSettings.Should().ContainKey("dotnet-counters");
        _ = config.Diagnostics.CollectorSettings["dotnet-counters"].Enabled.Should().BeTrue();

        // AnalyzerSettings
        _ = config.Diagnostics.AnalyzerSettings.Should().ContainKey("cpu-hotspots");
        AnalyzerSettingsEntry cpuAnalyzer = config.Diagnostics.AnalyzerSettings["cpu-hotspots"];
        _ = cpuAnalyzer.Enabled.Should().BeTrue();
        _ = cpuAnalyzer.Model.Should().Be("claude-opus-4.6");
        _ = cpuAnalyzer.MaxStacks.Should().Be(100);

        _ = config.Diagnostics.AnalyzerSettings.Should().ContainKey("memory-gc");
        AnalyzerSettingsEntry gcAnalyzer = config.Diagnostics.AnalyzerSettings["memory-gc"];
        _ = gcAnalyzer.Enabled.Should().BeTrue();
        _ = gcAnalyzer.Model.Should().Be("claude-opus-4.6");
    }

    // ── sample-api target config ─────────────────────────────────────────────

    [Fact]
    public void SampleApiConfig_LoadsCorrectly_WithExpectedOverrides()
    {
        string path = Path.Combine(RepoRoot, "sample-api", ".hone", "config.yaml");

        // ConfigLoader loads HoneConfig; TargetConfig fields (Name/BaseBranch/Hooks)
        // are ignored via IgnoreUnmatchedProperties — the HoneConfig override fields
        // (Api, ScaleTest) are what we validate here.
        HoneConfig config = ConfigLoader.Load(path);

        _ = config.Api.SolutionPath.Should().Be("SampleApi.sln");
        _ = config.Api.ProjectPath.Should().Be("SampleApi");
        _ = config.Api.TestProjectPath.Should().Be("SampleApi.Tests");
        _ = config.Api.BaseUrl.Should().Be("http://localhost:5050");
        _ = config.Api.HealthEndpoint.Should().Be("/health");
        _ = config.Api.GcEndpoint.Should().Be("/diag/gc");
        _ = config.Api.StartupTimeout.Should().Be(90);

        _ = config.ScaleTest.MeasuredRuns.Should().Be(5);
        _ = config.ScaleTest.WarmupEnabled.Should().BeTrue();
        _ = config.ScaleTest.CooldownSeconds.Should().Be(3);
    }
}
