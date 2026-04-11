using System.Text.Json;

using FluentAssertions;

using Hone.Agents.Core;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.TestInfrastructure;

using NSubstitute;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Agents.Preparation.Tests;

public sealed class MigratorAgentTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private readonly IAgentRunner _runner = Substitute.For<IAgentRunner>();

    private static AgentRunResult Ok(string json) =>
        new(Success: true, Output: json, TimedOut: false, ExitCode: 0);

    private MigratorAgent CreateSut(AgentConfig? config = null)
    {
        var invoker = new AgentInvoker(_runner, config ?? new AgentConfig());
        return new MigratorAgent(invoker);
    }

    private static CompatibilityReport CreateMinimalReport() =>
        new()
        {
            Compatibility = new CompatibilitySection
            {
                Overall = "compatible",
                Score = 80,
                Blockers = [],
                Warnings = [],
                Ready = [],
            },
            Target = new TargetSection
            {
                Name = "test-project",
                DetectedStack = "dotnet",
            },
            DetectedConfig = new DetectedConfigSection
            {
                Name = "test-project",
                BaseBranch = "main",
            },
        };

    private static PreProbeData CreatePreProbeWithLegacyHarness(string targetPath)
    {
        // Actual PS files are already created by the TargetBuilder
        string configPath = Path.Combine(targetPath, "config.psd1");
        string buildScript = Path.Combine(targetPath, "hooks", "Invoke-Build.ps1");

        return new PreProbeData
        {
            TargetPath = targetPath,
            Git = new GitInfo { IsGitRepo = true, DefaultBranch = "main" },
            ProjectFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["dotnet-sln"] = ["MyApp.sln"],
            },
            TopLevelDirs = ["src", "tests"],
            TopLevelFiles = ["MyApp.sln"],
            LegacyHarness = new LegacyHarnessInfo
            {
                Detected = true,
                ConfigPsd1Path = configPath,
                HookScripts = [buildScript],
            },
        };
    }

    // ── Reads PS files and includes content in prompt ────────────────────

    [Fact]
    public async Task MigrateAsync_ReadsPsFilesAndIncludesContentInPrompt()
    {
        string targetDir = CreateTargetDir("migrator-prompt", b => b
            .AddFile("MyApp.sln", "solution")
            .AddFile("config.psd1", "@{ Name = 'test-project'; BaseBranch = 'main' }")
            .AddFile("hooks/Invoke-Build.ps1", "dotnet build $SolutionPath --configuration Release"));

        PreProbeData preProbe = CreatePreProbeWithLegacyHarness(targetDir);
        CompatibilityReport report = CreateMinimalReport();

        string? capturedPrompt = null;
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedPrompt = callInfo.Arg<AgentInvocation>().Prompt;
                return Ok(JsonSerializer.Serialize(new MigrationPlan
                {
                    Config = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["Name"] = "test-project",
                    },
                    HookMappings = [],
                    Warnings = [],
                    Notes = "test",
                }));
            });

        MigratorAgent sut = CreateSut();
        _ = await sut.MigrateAsync(preProbe, report);

        _ = capturedPrompt.Should().NotBeNull();
        // Should contain config.psd1 content
        _ = capturedPrompt.Should().Contain("Name = 'test-project'");
        // Should contain hook script content
        _ = capturedPrompt.Should().Contain("dotnet build $SolutionPath");
        _ = capturedPrompt.Should().Contain("Invoke-Build.ps1");
    }

    // ── Returns plan on valid JSON response ─────────────────────────────

    [Fact]
    public async Task MigrateAsync_ValidJson_ReturnsPlan()
    {
        string targetDir = CreateTargetDir("migrator-ok", b => b
            .AddFile("MyApp.sln", "solution")
            .AddFile("config.psd1", "@{ Name = 'test-project'; BaseBranch = 'main' }")
            .AddFile("hooks/Invoke-Build.ps1", "dotnet build"));

        PreProbeData preProbe = CreatePreProbeWithLegacyHarness(targetDir);

        var plan = new MigrationPlan
        {
            Config = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["Name"] = "test-project",
                ["BaseBranch"] = "main",
            },
            HookMappings =
            [
                new HookMapping
                {
                    OriginalScript = "hooks/Invoke-Build.ps1",
                    MappedTo = "BuiltIn:DotnetBuild",
                    Confidence = "high",
                    Notes = "Direct match",
                },
            ],
            Warnings = ["Feature X not supported"],
            Notes = "Translated from PS config",
        };

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Ok(JsonSerializer.Serialize(plan)));

        MigratorAgent sut = CreateSut();
        MigrationResult result = await sut.MigrateAsync(preProbe, CreateMinimalReport());

        _ = result.Success.Should().BeTrue();
        _ = result.Plan.Should().NotBeNull();
        _ = result.Plan!.HookMappings.Should().HaveCount(1);
        _ = result.Plan.Warnings.Should().HaveCount(1);
        _ = result.Message.Should().Contain("1 hook mapping(s)");
        _ = result.Message.Should().Contain("1 warning(s)");
    }

    // ── Handles agent timeout gracefully ────────────────────────────────

    [Fact]
    public async Task MigrateAsync_AgentTimesOut_ReturnsFailure()
    {
        string targetDir = CreateTargetDir("migrator-timeout", b => b
            .AddFile("MyApp.sln", "solution")
            .AddFile("config.psd1", "@{ Name = 'test-project' }")
            .AddFile("hooks/Invoke-Build.ps1", "dotnet build"));

        PreProbeData preProbe = CreatePreProbeWithLegacyHarness(targetDir);

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult(
                Success: false, Output: "timed out", TimedOut: true, ExitCode: -1));

        MigratorAgent sut = CreateSut();
        MigrationResult result = await sut.MigrateAsync(preProbe, CreateMinimalReport());

        _ = result.Success.Should().BeFalse();
        _ = result.Message.Should().Contain("timed out");
        _ = result.Plan.Should().BeNull();
    }

    // ── Handles no legacy harness (null LegacyHarness) ──────────────────

    [Fact]
    public async Task MigrateAsync_NoLegacyHarness_StillProducesResult()
    {
        string targetDir = CreateTargetDir("migrator-nolegacy", b => b
            .AddFile("MyApp.sln", "solution"));

        var preProbe = new PreProbeData
        {
            TargetPath = targetDir,
            Git = new GitInfo { IsGitRepo = true, DefaultBranch = "main" },
            ProjectFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["dotnet-sln"] = ["MyApp.sln"],
            },
            TopLevelDirs = ["src", "tests"],
            TopLevelFiles = ["MyApp.sln"],
            LegacyHarness = null,
        };

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Ok(JsonSerializer.Serialize(new MigrationPlan
            {
                Config = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["Name"] = "test-project",
                },
                HookMappings = [],
                Notes = "No legacy config found",
            })));

        MigratorAgent sut = CreateSut();
        MigrationResult result = await sut.MigrateAsync(preProbe, CreateMinimalReport());

        _ = result.Success.Should().BeTrue();
        _ = result.Plan.Should().NotBeNull();
    }
}
