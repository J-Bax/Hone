using FluentAssertions;
using Hone.Core.Config;
using Hone.Lifecycle.Hooks;
using Hone.Lifecycle.Validation;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Lifecycle.Tests.Validation;

public sealed class ConfigValidatorTests(ITestOutputHelper output) : HoneTestBase(output)
{
    // ── Engine config: happy path ───────────────────────────────────────────

    [Fact]
    public void ValidateEngineConfig_ValidConfig_Passes()
    {
        HoneConfig config = new();

        ValidationResult result = ConfigValidator.ValidateEngineConfig(config);

        _ = result.IsValid.Should().BeTrue();
        _ = result.Errors.Should().BeEmpty();
    }

    // ── Engine config: tolerance validation ─────────────────────────────────

    [Theory]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public void ValidateEngineConfig_InvalidMaxRegressionPct_Fails(double value)
    {
        HoneConfig config = new(Tolerances: new TolerancesConfig(MaxRegressionPct: value));

        ValidationResult result = ConfigValidator.ValidateEngineConfig(config);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Errors.Should().ContainSingle(e => e.Contains("MaxRegressionPct"));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(2.0)]
    public void ValidateEngineConfig_InvalidMinImprovementPct_Fails(double value)
    {
        HoneConfig config = new(Tolerances: new TolerancesConfig(MinImprovementPct: value));

        ValidationResult result = ConfigValidator.ValidateEngineConfig(config);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Errors.Should().ContainSingle(e => e.Contains("MinImprovementPct"));
    }

    [Fact]
    public void ValidateEngineConfig_NegativeMinAbsoluteP95DeltaMs_Fails()
    {
        HoneConfig config = new(Tolerances: new TolerancesConfig(MinAbsoluteP95DeltaMs: -1));

        ValidationResult result = ConfigValidator.ValidateEngineConfig(config);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Errors.Should().ContainSingle(e => e.Contains("MinAbsoluteP95DeltaMs"));
    }

    [Fact]
    public void ValidateEngineConfig_StaleExperimentsZero_Fails()
    {
        HoneConfig config = new(Tolerances: new TolerancesConfig(StaleExperimentsBeforeStop: 0));

        ValidationResult result = ConfigValidator.ValidateEngineConfig(config);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Errors.Should().ContainSingle(e => e.Contains("StaleExperimentsBeforeStop"));
    }

    [Fact]
    public void ValidateEngineConfig_MaxConsecutiveFailuresZero_Fails()
    {
        HoneConfig config = new(Tolerances: new TolerancesConfig(MaxConsecutiveFailures: 0));

        ValidationResult result = ConfigValidator.ValidateEngineConfig(config);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Errors.Should().ContainSingle(e => e.Contains("MaxConsecutiveFailures"));
    }

    // ── Engine config: port validation ──────────────────────────────────────

    [Fact]
    public void ValidateEngineConfig_InvalidBaseUrl_Fails()
    {
        HoneConfig config = new(Api: new ApiConfig(BaseUrl: "not-a-url"));

        ValidationResult result = ConfigValidator.ValidateEngineConfig(config);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Errors.Should().ContainSingle(e => e.Contains("Api.BaseUrl"));
    }

    [Fact]
    public void ValidateEngineConfig_ValidPort_Passes()
    {
        HoneConfig config = new(Api: new ApiConfig(BaseUrl: "http://localhost:5000"));

        ValidationResult result = ConfigValidator.ValidateEngineConfig(config);

        _ = result.Errors.Should().NotContain(e => e.Contains("Api.BaseUrl"));
    }

    // ── Engine config: numeric ranges ───────────────────────────────────────

    [Fact]
    public void ValidateEngineConfig_InvalidStartupTimeout_Fails()
    {
        HoneConfig config = new(Api: new ApiConfig(StartupTimeout: 0));

        ValidationResult result = ConfigValidator.ValidateEngineConfig(config);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Errors.Should().ContainSingle(e => e.Contains("StartupTimeout"));
    }

    [Fact]
    public void ValidateEngineConfig_InvalidMeasuredRuns_Fails()
    {
        HoneConfig config = new(ScaleTest: new ScaleTestConfig(MeasuredRuns: 0));

        ValidationResult result = ConfigValidator.ValidateEngineConfig(config);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Errors.Should().ContainSingle(e => e.Contains("MeasuredRuns"));
    }

    // ── Engine config: implementer validation ───────────────────────────────

    [Fact]
    public void ValidateEngineConfig_InvalidImplementerMaxAttempts_Fails()
    {
        HoneConfig config = new(Implementer: new ImplementerConfig(MaxAttempts: 0));

        ValidationResult result = ConfigValidator.ValidateEngineConfig(config);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Errors.Should().ContainSingle(e => e.Contains("MaxAttempts"));
    }

    [Fact]
    public void ValidateEngineConfig_InvalidImplementerMaxDiffGrowthFactor_Fails()
    {
        HoneConfig config = new(Implementer: new ImplementerConfig(MaxDiffGrowthFactor: 0.5));

        ValidationResult result = ConfigValidator.ValidateEngineConfig(config);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Errors.Should().ContainSingle(e => e.Contains("MaxDiffGrowthFactor"));
    }

    // ── Engine config: interaction warnings ─────────────────────────────────

    [Fact]
    public void ValidateEngineConfig_StackedDiffsAndWaitForMerge_Warning()
    {
        HoneConfig config = new(Loop: new LoopConfig(StackedDiffs: true, WaitForMerge: true));

        ValidationResult result = ConfigValidator.ValidateEngineConfig(config);

        _ = result.IsValid.Should().BeTrue();
        _ = result.Warnings.Should().ContainSingle(w => w.Contains("StackedDiffs") && w.Contains("WaitForMerge"));
    }

    [Fact]
    public void ValidateEngineConfig_DiagnosticsEnabledNoRuns_Warning()
    {
        HoneConfig config = new(Diagnostics: new DiagnosticsConfig(Enabled: true, DiagnosticRuns: 0));

        ValidationResult result = ConfigValidator.ValidateEngineConfig(config);

        _ = result.IsValid.Should().BeTrue();
        _ = result.Warnings.Should().ContainSingle(w => w.Contains("DiagnosticRuns=0"));
    }

    [Fact]
    public void ValidateEngineConfig_SingleRunTightTolerance_Warning()
    {
        HoneConfig config = new(
            ScaleTest: new ScaleTestConfig(MeasuredRuns: 1),
            Tolerances: new TolerancesConfig(MaxRegressionPct: 0.01));

        ValidationResult result = ConfigValidator.ValidateEngineConfig(config);

        _ = result.IsValid.Should().BeTrue();
        _ = result.Warnings.Should().ContainSingle(w => w.Contains("MeasuredRuns=1") && w.Contains("tight tolerance"));
    }

    // ── Engine config: path validation ──────────────────────────────────────

    [Fact]
    public void ValidateEngineConfig_PathValidation_MissingPaths()
    {
        HoneConfig config = new(
            Api: new ApiConfig(
                SolutionPath: "nonexistent/solution.sln",
                ProjectPath: "nonexistent/project"));

        ValidationResult result = ConfigValidator.ValidateEngineConfig(config, rootPath: TempDir);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Errors.Should().Contain(e => e.Contains("SolutionPath"));
        _ = result.Errors.Should().Contain(e => e.Contains("ProjectPath"));
    }

    [Fact]
    public async Task ValidateEngineConfig_PathValidation_ExistingPaths()
    {
        // Create the expected paths
        string slnDir = Path.Combine(TempDir, "sample-api");
        Directory.CreateDirectory(slnDir);
        await File.WriteAllTextAsync(Path.Combine(slnDir, "SampleApi.sln"), "");
        Directory.CreateDirectory(Path.Combine(slnDir, "SampleApi"));
        string scenDir = Path.Combine(TempDir, "sample-api", "scale-tests", "scenarios");
        Directory.CreateDirectory(scenDir);
        await File.WriteAllTextAsync(Path.Combine(scenDir, "baseline.js"), "");

        HoneConfig config = new(
            Api: new ApiConfig(
                SolutionPath: "sample-api/SampleApi.sln",
                ProjectPath: "sample-api/SampleApi"),
            ScaleTest: new ScaleTestConfig(
                ScenarioPath: "sample-api/scale-tests/scenarios/baseline.js"));

        ValidationResult result = ConfigValidator.ValidateEngineConfig(config, rootPath: TempDir);

        _ = result.Errors.Should().NotContain(e => e.Contains("SolutionPath"));
        _ = result.Errors.Should().NotContain(e => e.Contains("ProjectPath"));
        _ = result.Errors.Should().NotContain(e => e.Contains("ScenarioPath"));
    }

    [Fact]
    public void ValidateEngineConfig_NoRootPath_SkipsPathValidation()
    {
        HoneConfig config = new(
            Api: new ApiConfig(
                SolutionPath: "nonexistent/solution.sln",
                ProjectPath: "nonexistent/project"));

        ValidationResult result = ConfigValidator.ValidateEngineConfig(config);

        _ = result.Errors.Should().NotContain(e => e.Contains("SolutionPath"));
        _ = result.Errors.Should().NotContain(e => e.Contains("ProjectPath"));
    }

    // ── Target config: happy path ───────────────────────────────────────────

    [Fact]
    public void ValidateTargetConfig_ValidConfig_Passes()
    {
        TargetConfig config = CreateValidTargetConfig();

        ValidationResult result = ConfigValidator.ValidateTargetConfig(config, TempDir);

        _ = result.IsValid.Should().BeTrue();
        _ = result.Errors.Should().BeEmpty();
    }

    // ── Target config: missing name ─────────────────────────────────────────

    [Fact]
    public void ValidateTargetConfig_MissingName_Fails()
    {
        TargetConfig config = CreateValidTargetConfig() with { Name = "" };

        ValidationResult result = ConfigValidator.ValidateTargetConfig(config, TempDir);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Errors.Should().ContainSingle(e => e.Contains("Name"));
    }

    // ── Target config: hook validation ──────────────────────────────────────

    [Fact]
    public void ValidateTargetConfig_MissingHooks_Fails()
    {
        TargetConfig config = new(Name: "test-target");

        ValidationResult result = ConfigValidator.ValidateTargetConfig(config, TempDir);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Errors.Should().HaveCount(8);
        _ = result.Errors.Should().AllSatisfy(e => e.Should().Contain("is not declared"));
    }

    [Fact]
    public void ValidateTargetConfig_InvalidHookType_Fails()
    {
        TargetConfig config = CreateTargetConfigWithHook("Prepare", new TargetHookConfig(Type: "FooBar"));

        ValidationResult result = ConfigValidator.ValidateTargetConfig(config, TempDir);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Errors.Should().Contain(e => e.Contains("invalid Type 'FooBar'"));
    }

    [Fact]
    public void ValidateTargetConfig_MissingCommandValue_Fails()
    {
        TargetConfig config = CreateTargetConfigWithHook("Prepare", new TargetHookConfig(Type: "Command"));

        ValidationResult result = ConfigValidator.ValidateTargetConfig(config, TempDir);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Errors.Should().Contain(e => e.Contains("missing Value for Command hook"));
    }

    [Fact]
    public void ValidateTargetConfig_MissingHttpPath_Fails()
    {
        TargetConfig config = CreateTargetConfigWithHook("Prepare", new TargetHookConfig(Type: "Http"));

        ValidationResult result = ConfigValidator.ValidateTargetConfig(config, TempDir);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Errors.Should().Contain(e => e.Contains("missing Path for Http hook"));
    }

    [Fact]
    public void ValidateTargetConfig_MissingHookType_Fails()
    {
        TargetConfig config = CreateTargetConfigWithHook("Prepare", new TargetHookConfig(Type: ""));

        ValidationResult result = ConfigValidator.ValidateTargetConfig(config, TempDir);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Errors.Should().Contain(e => e.Contains("Hooks.Prepare is missing Type"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static TargetConfig CreateValidTargetConfig()
    {
        Dictionary<string, TargetHookConfig> hooks = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Prepare"] = new TargetHookConfig(Type: "BuiltIn"),
            ["Start"] = new TargetHookConfig(Type: "Command", Value: "dotnet run"),
            ["Stop"] = new TargetHookConfig(Type: "Command", Value: "kill"),
            ["Ready"] = new TargetHookConfig(Type: "Http", Path: "/health"),
            ["Warmup"] = new TargetHookConfig(Type: "Skip"),
            ["Active"] = new TargetHookConfig(Type: "Skip"),
            ["Cooldown"] = new TargetHookConfig(Type: "Skip"),
            ["Cleanup"] = new TargetHookConfig(Type: "BuiltIn"),
        };

        return new TargetConfig(Name: "test-target", Hooks: hooks);
    }

    /// <summary>
    /// Creates a target config with all 8 hooks valid (BuiltIn) except the specified one
    /// which is overridden with the given config, for isolated hook validation tests.
    /// </summary>
    private static TargetConfig CreateTargetConfigWithHook(string hookName, TargetHookConfig hookConfig)
    {
        Dictionary<string, TargetHookConfig> hooks = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Prepare"] = new TargetHookConfig(Type: "BuiltIn"),
            ["Start"] = new TargetHookConfig(Type: "BuiltIn"),
            ["Stop"] = new TargetHookConfig(Type: "BuiltIn"),
            ["Ready"] = new TargetHookConfig(Type: "BuiltIn"),
            ["Warmup"] = new TargetHookConfig(Type: "BuiltIn"),
            ["Active"] = new TargetHookConfig(Type: "BuiltIn"),
            ["Cooldown"] = new TargetHookConfig(Type: "BuiltIn"),
            ["Cleanup"] = new TargetHookConfig(Type: "BuiltIn"),
        };

        hooks[hookName] = hookConfig;

        return new TargetConfig(Name: "test-target", Hooks: hooks);
    }
}
