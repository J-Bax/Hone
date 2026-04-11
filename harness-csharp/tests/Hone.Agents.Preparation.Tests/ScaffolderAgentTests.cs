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

public sealed class ScaffolderAgentTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private readonly IAgentRunner _runner = Substitute.For<IAgentRunner>();

    private static AgentRunResult Ok(string json) =>
        new(Success: true, Output: json, TimedOut: false, ExitCode: 0);

    private ScaffolderAgent CreateSut(AgentConfig? config = null)
    {
        var invoker = new AgentInvoker(_runner, config ?? new AgentConfig());
        return new ScaffolderAgent(invoker);
    }

    private static CompatibilityReport CreateMinimalReport(int score = 80) =>
        new()
        {
            Compatibility = new CompatibilitySection
            {
                Overall = "compatible",
                Score = score,
                Blockers = [],
                Warnings = [],
                Ready = [],
            },
            Target = new TargetSection
            {
                Name = "test-project",
                DetectedStack = "dotnet",
                DetectedFramework = "ASP.NET Core 8.0",
            },
            DetectedConfig = new DetectedConfigSection
            {
                Name = "test-project",
                BaseBranch = "main",
                HealthEndpoint = "/health",
            },
        };

    private static PreProbeData CreateMinimalPreProbe(string targetPath) =>
        new()
        {
            TargetPath = targetPath,
            Git = new GitInfo { IsGitRepo = true, DefaultBranch = "main" },
            ProjectFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["dotnet-sln"] = ["MyApp.sln"],
            },
            TopLevelDirs = ["src", "tests"],
            TopLevelFiles = ["MyApp.sln"],
        };

    // ── Sends report data in prompt ─────────────────────────────────────

    [Fact]
    public async Task ScaffoldAsync_SendsReportDataInPrompt()
    {
        string targetDir = CreateTargetDir("scaffold-prompt", b => b
            .AddFile("MyApp.sln", "solution"));

        CompatibilityReport report = CreateMinimalReport();
        PreProbeData preProbe = CreateMinimalPreProbe(targetDir);

        string? capturedPrompt = null;
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedPrompt = callInfo.Arg<AgentInvocation>().Prompt;
                return Ok(JsonSerializer.Serialize(new ScaffoldPlan
                {
                    Files = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [".hone/config.yaml"] = "Name: test",
                    },
                    Notes = "test",
                }));
            });

        ScaffolderAgent sut = CreateSut();
        _ = await sut.ScaffoldAsync(report, preProbe);

        _ = capturedPrompt.Should().NotBeNull();
        _ = capturedPrompt.Should().Contain("test-project");
        _ = capturedPrompt.Should().Contain("dotnet");
        _ = capturedPrompt.Should().Contain("ASP.NET Core 8.0");
        _ = capturedPrompt.Should().Contain("MyApp.sln");
    }

    // ── Returns plan on valid JSON ──────────────────────────────────────

    [Fact]
    public async Task ScaffoldAsync_ValidJson_ReturnsPlan()
    {
        string targetDir = CreateTargetDir("scaffold-ok", b => b
            .AddFile("MyApp.sln", "solution"));

        var plan = new ScaffoldPlan
        {
            Files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".hone/config.yaml"] = "Name: test-project\nBaseBranch: main",
                [".hone/scenarios/baseline.js"] = "import http from 'k6/http';",
            },
            Notes = "Generated for .NET project",
        };

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Ok(JsonSerializer.Serialize(plan)));

        ScaffolderAgent sut = CreateSut();
        ScaffoldResult result = await sut.ScaffoldAsync(
            CreateMinimalReport(), CreateMinimalPreProbe(targetDir));

        _ = result.Success.Should().BeTrue();
        _ = result.Plan.Should().NotBeNull();
        _ = result.Plan!.Files.Should().HaveCount(2);
        _ = result.Plan.Files!.Keys.Should().Contain(".hone/config.yaml");
        _ = result.Plan.Notes.Should().Be("Generated for .NET project");
    }

    // ── Handles invalid JSON gracefully ─────────────────────────────────

    [Fact]
    public async Task ScaffoldAsync_InvalidJson_ReturnsFailure()
    {
        string targetDir = CreateTargetDir("scaffold-badjson", b => b
            .AddFile("MyApp.sln", "solution"));

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Ok("not json at all"), Ok("still not json"));

        ScaffolderAgent sut = CreateSut();
        ScaffoldResult result = await sut.ScaffoldAsync(
            CreateMinimalReport(), CreateMinimalPreProbe(targetDir));

        _ = result.Success.Should().BeFalse();
        _ = result.Message.Should().Contain("not valid JSON");
    }

    // ── Handles agent timeout ───────────────────────────────────────────

    [Fact]
    public async Task ScaffoldAsync_AgentTimesOut_ReturnsFailure()
    {
        string targetDir = CreateTargetDir("scaffold-timeout", b => b
            .AddFile("MyApp.sln", "solution"));

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult(
                Success: false, Output: "timed out", TimedOut: true, ExitCode: -1));

        ScaffolderAgent sut = CreateSut();
        ScaffoldResult result = await sut.ScaffoldAsync(
            CreateMinimalReport(), CreateMinimalPreProbe(targetDir));

        _ = result.Success.Should().BeFalse();
        _ = result.Message.Should().Contain("timed out");
    }
}
