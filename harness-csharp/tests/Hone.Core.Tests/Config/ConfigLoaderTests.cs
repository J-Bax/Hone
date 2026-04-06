using FluentAssertions;
using Hone.Core.Config;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Config;

public sealed class ConfigLoaderTests(ITestOutputHelper output) : HoneTestBase(output)
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private string WriteYaml(string fileName, string content)
    {
        string path = Path.Combine(TempDir, fileName);

#pragma warning disable RS0030 // Sync I/O is intentional in test setup
        File.WriteAllText(path, content);
#pragma warning restore RS0030

        return path;
    }

    // ── YAML fixtures ───────────────────────────────────────────────────────

    private const string EngineConfigYaml = """
        Api:
          SolutionPath: "custom-api/Api.sln"
          ProjectPath: "custom-api/Api"
          SourceFileGlob: "*.cs"
          TestProjectPath: "custom-api/Api.Tests"
          BaseUrl: "http://localhost:9000"
          HealthEndpoint: "/health"
          GcEndpoint: "/diag/gc"
          StartupTimeout: 120
          ResultsPath: "custom-api/.hone/results"
          MetadataPath: "custom-api/.hone/results/metadata"
        Tolerances:
          MaxRegressionPct: 0.15
          MinAbsoluteP95DeltaMs: 10
          MinAbsoluteRPSDelta: 8
          MinAbsoluteErrorRateDelta: 0.01
          MinImprovementPct: 0.02
          StaleExperimentsBeforeStop: 3
          MaxConsecutiveFailures: 5
          Efficiency:
            Enabled: false
            MinCpuReductionPct: 0.10
            MinWorkingSetReductionPct: 0.08
        ScaleTest:
          ScenarioPath: "custom/baseline.js"
          ScenarioRegistryPath: "custom/thresholds.json"
          WarmupEnabled: false
          WarmupScenarioPath: "custom/warmup.js"
          MeasuredRuns: 3
          CooldownSeconds: 5
        Loop:
          MaxExperiments: 50
          BranchPrefix: "opt/exp"
          StackedDiffs: false
          WaitForMerge: true
          SkipClassification: true
        Agents:
          DefaultModel: "gpt-4o"
          AnalysisModel: "gpt-4o"
          ClassificationModel: "gpt-4o"
          ImplementerModel: "gpt-4o"
          AgentTimeoutSec: 900
        Diagnostics:
          Enabled: false
          CollectorsPath: "custom/collectors"
          AnalyzersPath: "custom/analyzers"
          DiagnosticRuns: 2
          K6TimeoutSec: 600
        Logging:
          Level: "verbose"
          MaxFileSizeMB: 100
        Implementer:
          MaxAttempts: 5
          MaxDiffGrowthFactor: 2.0
          TestFileGuard: false
        DotnetCounters:
          Enabled: false
          RefreshIntervalSeconds: 5
        """;

    private const string PartialConfigYaml = """
        Api:
          BaseUrl: "http://localhost:5050"
        Loop:
          MaxExperiments: 5
        """;

    private const string MalformedYaml = """
        Api:
          BaseUrl: [invalid
        """;

    // ── ConfigLoader_LoadsYaml_AllSectionsPresent ───────────────────────────

    [Fact]
    public void Load_AllSectionsPresent_PopulatesAllSections()
    {
        string path = WriteYaml("engine.yaml", EngineConfigYaml);

        HoneConfig config = ConfigLoader.Load(path);

        // Api
        _ = config.Api.SolutionPath.Should().Be("custom-api/Api.sln");
        _ = config.Api.ProjectPath.Should().Be("custom-api/Api");
        _ = config.Api.BaseUrl.Should().Be("http://localhost:9000");
        _ = config.Api.StartupTimeout.Should().Be(120);
        _ = config.Api.ResultsPath.Should().Be("custom-api/.hone/results");

        // Tolerances
        _ = config.Tolerances.MaxRegressionPct.Should().Be(0.15);
        _ = config.Tolerances.MinAbsoluteP95DeltaMs.Should().Be(10);
        _ = config.Tolerances.StaleExperimentsBeforeStop.Should().Be(3);
        _ = config.Tolerances.Efficiency.Enabled.Should().BeFalse();
        _ = config.Tolerances.Efficiency.MinCpuReductionPct.Should().Be(0.10);

        // ScaleTest
        _ = config.ScaleTest.ScenarioPath.Should().Be("custom/baseline.js");
        _ = config.ScaleTest.MeasuredRuns.Should().Be(3);
        _ = config.ScaleTest.WarmupEnabled.Should().BeFalse();

        // Loop
        _ = config.Loop.MaxExperiments.Should().Be(50);
        _ = config.Loop.BranchPrefix.Should().Be("opt/exp");
        _ = config.Loop.StackedDiffs.Should().BeFalse();
        _ = config.Loop.WaitForMerge.Should().BeTrue();

        // Agents
        _ = config.Agents.DefaultModel.Should().Be("gpt-4o");
        _ = config.Agents.AgentTimeoutSec.Should().Be(900);

        // Diagnostics
        _ = config.Diagnostics.Enabled.Should().BeFalse();
        _ = config.Diagnostics.CollectorsPath.Should().Be("custom/collectors");
        _ = config.Diagnostics.DiagnosticRuns.Should().Be(2);

        // Logging
        _ = config.Logging.Level.Should().Be("verbose");
        _ = config.Logging.MaxFileSizeMB.Should().Be(100);

        // Implementer
        _ = config.Implementer.MaxAttempts.Should().Be(5);
        _ = config.Implementer.MaxDiffGrowthFactor.Should().Be(2.0);
        _ = config.Implementer.TestFileGuard.Should().BeFalse();

        // DotnetCounters
        _ = config.DotnetCounters.Enabled.Should().BeFalse();
        _ = config.DotnetCounters.RefreshIntervalSeconds.Should().Be(5);
    }

    // ── ConfigLoader_MissingOptionalSections_UsesDefaults ───────────────────

    [Fact]
    public void Load_MissingOptionalSections_UsesDefaults()
    {
        string path = WriteYaml("partial.yaml", PartialConfigYaml);

        HoneConfig config = ConfigLoader.Load(path);

        // Explicitly set values
        _ = config.Api.BaseUrl.Should().Be("http://localhost:5050");
        _ = config.Loop.MaxExperiments.Should().Be(5);

        // Missing sections should have defaults
        _ = config.Tolerances.MaxRegressionPct.Should().Be(0.10);
        _ = config.ScaleTest.MeasuredRuns.Should().Be(5);
        _ = config.Agents.DefaultModel.Should().Be("claude-sonnet-4.5");
        _ = config.Diagnostics.Enabled.Should().BeTrue();
        _ = config.Logging.Level.Should().Be("info");
        _ = config.Implementer.MaxAttempts.Should().Be(3);
        _ = config.DotnetCounters.Enabled.Should().BeTrue();

        // Unset properties within partially-specified sections should have defaults
        _ = config.Api.SolutionPath.Should().Be("sample-api/SampleApi.sln");
        _ = config.Api.StartupTimeout.Should().Be(90);
        _ = config.Loop.BranchPrefix.Should().Be("hone/experiment");
        _ = config.Loop.StackedDiffs.Should().BeTrue();
    }

    // ── ConfigLoader_MissingFile_ThrowsDescriptiveError ─────────────────────

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFoundException()
    {
        string missingPath = Path.Combine(TempDir, "nonexistent.yaml");

        Action act = () => ConfigLoader.Load(missingPath);

        _ = act.Should().Throw<FileNotFoundException>()
            .WithMessage($"*{missingPath}*");
    }

    // ── ConfigLoader_MalformedYaml_ThrowsParseError ─────────────────────────

    [Fact]
    public void Load_MalformedYaml_ThrowsInvalidOperationException()
    {
        string path = WriteYaml("malformed.yaml", MalformedYaml);

        Action act = () => ConfigLoader.Load(path);

        _ = act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{path}*");
    }

    // ── ConfigLoader_EmptyFile_ReturnsDefaults ──────────────────────────────

    [Fact]
    public void Load_EmptyFile_ReturnsDefaults()
    {
        string path = WriteYaml("empty.yaml", string.Empty);

        HoneConfig config = ConfigLoader.Load(path);

        _ = config.Should().NotBeNull();
        _ = config.Api.SolutionPath.Should().Be("sample-api/SampleApi.sln");
        _ = config.Api.BaseUrl.Should().Be("http://localhost:0");
        _ = config.Tolerances.MaxRegressionPct.Should().Be(0.10);
        _ = config.Loop.MaxExperiments.Should().Be(999);
        _ = config.Agents.DefaultModel.Should().Be("claude-sonnet-4.5");
        _ = config.Diagnostics.Enabled.Should().BeTrue();
        _ = config.Logging.Level.Should().Be("info");
        _ = config.Implementer.MaxAttempts.Should().Be(3);
        _ = config.DotnetCounters.Enabled.Should().BeTrue();
    }

    // ── Whitespace-only file also returns defaults ──────────────────────────

    [Fact]
    public void Load_WhitespaceOnlyFile_ReturnsDefaults()
    {
        string path = WriteYaml("whitespace.yaml", "   \n  \n  ");

        HoneConfig config = ConfigLoader.Load(path);

        _ = config.Should().NotBeNull();
        _ = config.Api.BaseUrl.Should().Be("http://localhost:0");
    }
}
