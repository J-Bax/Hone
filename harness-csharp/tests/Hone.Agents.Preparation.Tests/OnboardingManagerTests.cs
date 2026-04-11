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

public sealed class OnboardingManagerTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private readonly IAgentRunner _runner = Substitute.For<IAgentRunner>();

    private static AgentRunResult Ok(string json) =>
        new(Success: true, Output: json, TimedOut: false, ExitCode: 0);

    private OnboardingManager CreateSut(AgentConfig? config = null)
    {
        var invoker = new AgentInvoker(_runner, config ?? new AgentConfig());
        var assessor = new CompatibilityAgent(invoker);
        var scaffolder = new ScaffolderAgent(invoker);
        return new OnboardingManager(assessor, scaffolder);
    }

    private static string MakeAssessmentJson(int score = 80, string overall = "compatible") =>
        JsonSerializer.Serialize(new CompatibilityReport
        {
            Compatibility = new CompatibilitySection
            {
                Overall = overall,
                Score = score,
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
        });

    private static string MakeScaffoldJson() =>
        JsonSerializer.Serialize(new ScaffoldPlan
        {
            Files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".hone/config.yaml"] = "Name: test-project\nBaseBranch: main",
                [".hone/scenarios/baseline.js"] = "import http from 'k6/http';",
            },
            Notes = "Generated config",
        });

    // ── Full flow: assess + scaffold + write ────────────────────────────

    [Fact]
    public async Task OnboardAsync_FullFlow_WritesFiles()
    {
        string targetDir = CreateTargetDir("onboard-full", b => b
            .AddFile("MyApp.sln", "solution"));

        int callCount = 0;
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                int call = Interlocked.Increment(ref callCount);
                return call == 1
                    ? Ok(MakeAssessmentJson())
                    : Ok(MakeScaffoldJson());
            });

        OnboardingManager sut = CreateSut();
        OnboardingResult result = await sut.OnboardAsync(
            targetDir, new OnboardingOptions());

        _ = result.Success.Should().BeTrue();
        _ = result.Assessment.Should().NotBeNull();
        _ = result.Assessment!.Success.Should().BeTrue();
        _ = result.Scaffold.Should().NotBeNull();
        _ = result.Scaffold!.Success.Should().BeTrue();
        _ = result.WriteResult.Should().NotBeNull();
        _ = result.WriteResult!.Written.Should().HaveCount(2);

        _ = File.Exists(Path.Combine(targetDir, ".hone", "config.yaml")).Should().BeTrue();
        _ = File.Exists(Path.Combine(targetDir, ".hone", "scenarios", "baseline.js")).Should().BeTrue();
    }

    // ── Low score without force returns early ───────────────────────────

    [Fact]
    public async Task OnboardAsync_LowScoreWithoutForce_ReturnsEarly()
    {
        string targetDir = CreateTargetDir("onboard-lowscore", b => b
            .AddFile("MyApp.sln", "solution"));

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Ok(MakeAssessmentJson(score: 20, overall: "incompatible")));

        OnboardingManager sut = CreateSut();
        OnboardingResult result = await sut.OnboardAsync(
            targetDir, new OnboardingOptions());

        _ = result.Success.Should().BeFalse();
        _ = result.Message.Should().Contain("Score too low");
        _ = result.Message.Should().Contain("--force");
        _ = result.Assessment.Should().NotBeNull();
        _ = result.Scaffold.Should().BeNull();
        _ = result.WriteResult.Should().BeNull();
    }

    // ── Low score with force proceeds ───────────────────────────────────

    [Fact]
    public async Task OnboardAsync_LowScoreWithForce_Proceeds()
    {
        string targetDir = CreateTargetDir("onboard-force", b => b
            .AddFile("MyApp.sln", "solution"));

        int callCount = 0;
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                int call = Interlocked.Increment(ref callCount);
                return call == 1
                    ? Ok(MakeAssessmentJson(score: 20, overall: "incompatible"))
                    : Ok(MakeScaffoldJson());
            });

        OnboardingManager sut = CreateSut();
        OnboardingResult result = await sut.OnboardAsync(
            targetDir, new OnboardingOptions(Force: true));

        _ = result.Success.Should().BeTrue();
        _ = result.WriteResult.Should().NotBeNull();
        _ = result.WriteResult!.Written.Should().HaveCount(2);
    }

    // ── Dry run returns plan without writing files ──────────────────────

    [Fact]
    public async Task OnboardAsync_DryRun_ReturnsPlanWithoutWritingFiles()
    {
        string targetDir = CreateTargetDir("onboard-dryrun", b => b
            .AddFile("MyApp.sln", "solution"));

        int callCount = 0;
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                int call = Interlocked.Increment(ref callCount);
                return call == 1
                    ? Ok(MakeAssessmentJson())
                    : Ok(MakeScaffoldJson());
            });

        OnboardingManager sut = CreateSut();
        OnboardingResult result = await sut.OnboardAsync(
            targetDir, new OnboardingOptions(DryRun: true));

        _ = result.Success.Should().BeTrue();
        _ = result.Message.Should().Contain("Dry run");
        _ = result.Scaffold.Should().NotBeNull();
        _ = result.Scaffold!.Plan.Should().NotBeNull();
        _ = result.Scaffold.Plan!.Files.Should().HaveCount(2);
        _ = result.WriteResult.Should().BeNull();

        // Files should NOT exist on disk
        _ = File.Exists(Path.Combine(targetDir, ".hone", "config.yaml")).Should().BeFalse();
    }

    // ── Assessment failure returns early ─────────────────────────────────

    [Fact]
    public async Task OnboardAsync_AssessmentFailure_ReturnsEarly()
    {
        string nonExistent = Path.Combine(TempDir, "does-not-exist");

        OnboardingManager sut = CreateSut();
        OnboardingResult result = await sut.OnboardAsync(
            nonExistent, new OnboardingOptions());

        _ = result.Success.Should().BeFalse();
        _ = result.Message.Should().Contain("Assessment failed");
        _ = result.Assessment.Should().NotBeNull();
        _ = result.Assessment!.Success.Should().BeFalse();
        _ = result.Scaffold.Should().BeNull();
        _ = result.WriteResult.Should().BeNull();
    }
}
