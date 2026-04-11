using System.Text.Json;

using FluentAssertions;

using Hone.Agents.Core;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.TestInfrastructure;

using NSubstitute;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Agents.Preparation.Tests;

public sealed class CompatibilityAgentTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private readonly IAgentRunner _runner = Substitute.For<IAgentRunner>();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();

    private static AgentRunResult Ok(string json) =>
        new(Success: true, Output: json, TimedOut: false, ExitCode: 0);

    private CompatibilityAgent CreateSut(AgentConfig? config = null, IProcessRunner? processRunner = null)
    {
        var invoker = new AgentInvoker(_runner, config ?? new AgentConfig());
        return new CompatibilityAgent(invoker, processRunner);
    }

    // ── Compatible target ───────────────────────────────────────────────

    [Fact]
    public async Task CompatibilityAgent_CompatibleTarget_ReturnsSuccess()
    {
        string targetDir = CreateTargetDir("compat-ok", b => b
            .AddFile("MyApp.sln", "solution")
            .AddFile("src/MyApp/MyApp.csproj", "<Project />"));

        var report = new CompatibilityReport
        {
            Compatibility = new CompatibilitySection
            {
                Overall = "compatible",
                Score = 95,
                Blockers = [],
                Warnings = [],
                Ready =
                [
                    new ReadyItem { Area = "build", Detail = "dotnet build succeeds" },
                ],
            },
            Target = new TargetSection
            {
                DetectedStack = ".NET",
                DetectedFramework = "ASP.NET Core 8.0",
            },
            OnboardingPlan = new OnboardingPlanSection
            {
                Summary = "Ready for onboarding",
            },
        };

        string json = JsonSerializer.Serialize(report);

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Ok(json));

        CompatibilityAgent sut = CreateSut();
        CompatibilityResult result = await sut.AssessAsync(targetDir);

        _ = result.Success.Should().BeTrue();
        _ = result.Message.Should().Contain("COMPATIBLE");
        _ = result.Message.Should().Contain("95/100");
        _ = result.Report.Should().NotBeNull();
        _ = result.Report!.Compatibility!.Overall.Should().Be("compatible");
        _ = result.Report.Compatibility.Score.Should().Be(95);
        _ = result.Report.Compatibility.Ready.Should().HaveCount(1);
        _ = result.Report.Target!.DetectedStack.Should().Be(".NET");
    }

    // ── Incompatible target ─────────────────────────────────────────────

    [Fact]
    public async Task CompatibilityAgent_IncompatibleTarget_ReturnsFailureReport()
    {
        string targetDir = CreateTargetDir("incompat", b => b
            .AddFile("package.json", "{}"));

        var report = new CompatibilityReport
        {
            Compatibility = new CompatibilitySection
            {
                Overall = "incompatible",
                Score = 20,
                Blockers =
                [
                    new CompatibilityFinding
                    {
                        Area = "build",
                        Issue = "Build fails with errors",
                        Remediation = "Fix compilation errors",
                    },
                ],
                Warnings = [],
                Ready = [],
            },
        };

        string json = JsonSerializer.Serialize(report);

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Ok(json));

        CompatibilityAgent sut = CreateSut();
        CompatibilityResult result = await sut.AssessAsync(targetDir);

        // The agent returned valid JSON, so Success is true (the assessment completed).
        // The *report itself* indicates incompatibility via the overall field.
        _ = result.Success.Should().BeTrue();
        _ = result.Message.Should().Contain("INCOMPATIBLE");
        _ = result.Message.Should().Contain("20/100");
        _ = result.Report!.Compatibility!.Blockers.Should().HaveCount(1);
        _ = result.Report.Compatibility.Blockers![0].Area.Should().Be("build");
    }

    // ── Pre-probe detects project files ─────────────────────────────────

    [Fact]
    public async Task CompatibilityAgent_PreProbe_DetectsProjectFiles()
    {
        string targetDir = CreateTargetDir("probe-files", b => b
            .AddFile("MyApp.sln", "solution content")
            .AddFile("src/MyApp/MyApp.csproj", "<Project />")
            .AddFile("global.json", "{}")
            .AddFile("package.json", "{}")
            .AddDirectory("src")
            .AddDirectory("tests")
            .AddFile("README.md", "# readme"));

        // We spy on what prompt the agent receives to verify pre-probe content
        string? capturedPrompt = null;
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                AgentInvocation invocation = callInfo.Arg<AgentInvocation>();
                capturedPrompt = invocation.Prompt;
                return Ok(JsonSerializer.Serialize(new CompatibilityReport
                {
                    Compatibility = new CompatibilitySection { Overall = "compatible", Score = 80 },
                }));
            });

        CompatibilityAgent sut = CreateSut();
        _ = await sut.AssessAsync(targetDir);

        _ = capturedPrompt.Should().NotBeNull();
        _ = capturedPrompt.Should().Contain("dotnet-sln");
        _ = capturedPrompt.Should().Contain("dotnet-csproj");
        _ = capturedPrompt.Should().Contain("dotnet-global");
        _ = capturedPrompt.Should().Contain("node-package");
        _ = capturedPrompt.Should().Contain("README.md");
    }

    // ── Invalid target path ─────────────────────────────────────────────

    [Fact]
    public async Task CompatibilityAgent_InvalidTargetPath_ReturnsFailure()
    {
        string nonExistent = Path.Combine(TempDir, "does-not-exist");

        CompatibilityAgent sut = CreateSut();
        CompatibilityResult result = await sut.AssessAsync(nonExistent);

        _ = result.Success.Should().BeFalse();
        _ = result.Message.Should().Contain("Target directory not found");
        _ = result.Report.Should().BeNull();
    }

    // ── Agent timeout ───────────────────────────────────────────────────

    [Fact]
    public async Task CompatibilityAgent_AgentTimesOut_ReturnsFailure()
    {
        string targetDir = CreateTargetDir("timeout", b => b
            .AddFile("MyApp.sln", "solution"));

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult(
                Success: false, Output: "timed out", TimedOut: true, ExitCode: -1));

        CompatibilityAgent sut = CreateSut();
        CompatibilityResult result = await sut.AssessAsync(targetDir);

        _ = result.Success.Should().BeFalse();
        _ = result.Message.Should().Contain("timed out");
    }

    // ── Agent returns invalid JSON ──────────────────────────────────────

    [Fact]
    public async Task CompatibilityAgent_InvalidJson_ReturnsFailure()
    {
        string targetDir = CreateTargetDir("badjson", b => b
            .AddFile("MyApp.sln", "solution"));

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Ok("not json at all"), Ok("still not json"));

        CompatibilityAgent sut = CreateSut();
        CompatibilityResult result = await sut.AssessAsync(targetDir);

        _ = result.Success.Should().BeFalse();
        _ = result.Message.Should().Contain("not valid JSON");
    }

    // ── Model and agent name passed correctly ───────────────────────────

    [Fact]
    public async Task CompatibilityAgent_DefaultModel_PassedToInvoker()
    {
        string targetDir = CreateTargetDir("model-test", b => b
            .AddFile("MyApp.sln", "solution"));

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Ok(JsonSerializer.Serialize(new CompatibilityReport
            {
                Compatibility = new CompatibilitySection { Overall = "compatible", Score = 100 },
            })));

        CompatibilityAgent sut = CreateSut();
        _ = await sut.AssessAsync(targetDir);

        _ = await _runner.Received(1).InvokeAsync(
            Arg.Is<AgentInvocation>(inv => inv.AgentName == "hone-compatibility"),
            Arg.Any<CancellationToken>());
    }

    // ── Model override ──────────────────────────────────────────────────

    [Fact]
    public async Task CompatibilityAgent_ModelOverride_UsedByInvoker()
    {
        string targetDir = CreateTargetDir("model-override", b => b
            .AddFile("MyApp.sln", "solution"));

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Ok(JsonSerializer.Serialize(new CompatibilityReport
            {
                Compatibility = new CompatibilitySection { Overall = "compatible", Score = 100 },
            })));

        // Config has no AnalysisModel override, so DefaultModel from options is used
        var config = new AgentConfig(AnalysisModel: null);
        CompatibilityAgent sut = CreateSut(config);
        _ = await sut.AssessAsync(targetDir, model: "my-custom-model");

        _ = await _runner.Received(1).InvokeAsync(
            Arg.Is<AgentInvocation>(inv => inv.Model == "my-custom-model"),
            Arg.Any<CancellationToken>());
    }

    // ── Pre-probe git info via IProcessRunner ───────────────────────────

    [Fact]
    public async Task CompatibilityAgent_GitProbe_IncludedInPrompt()
    {
        string targetDir = CreateTargetDir("git-probe", b => b
            .AddFile("MyApp.sln", "solution"));

        // Mock git commands
        _ = _processRunner.RunAsync(
            "git", Arg.Is<IEnumerable<string>>(a => a.First() == "rev-parse"),
            Arg.Any<string?>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(Success: true, Output: ".git", ExitCode: 0, TimedOut: false));

        _ = _processRunner.RunAsync(
            "git", Arg.Is<IEnumerable<string>>(a => a.First() == "remote"),
            Arg.Any<string?>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(Success: true, Output: "origin\thttps://github.com/test/repo.git (fetch)", ExitCode: 0, TimedOut: false));

        _ = _processRunner.RunAsync(
            "git", Arg.Is<IEnumerable<string>>(a => a.First() == "symbolic-ref"),
            Arg.Any<string?>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(Success: true, Output: "refs/remotes/origin/main", ExitCode: 0, TimedOut: false));

        string? capturedPrompt = null;
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedPrompt = callInfo.Arg<AgentInvocation>().Prompt;
                return Ok(JsonSerializer.Serialize(new CompatibilityReport
                {
                    Compatibility = new CompatibilitySection { Overall = "compatible", Score = 100 },
                }));
            });

        CompatibilityAgent sut = CreateSut(processRunner: _processRunner);
        _ = await sut.AssessAsync(targetDir);

        _ = capturedPrompt.Should().NotBeNull();
        _ = capturedPrompt.Should().Contain("\"isGitRepo\":true");
        _ = capturedPrompt.Should().Contain("main");
    }

    // ── Pre-probe top-level dirs exclude hidden and special ─────────────

    [Fact]
    public async Task CompatibilityAgent_PreProbe_ExcludesHiddenAndSpecialDirs()
    {
        string targetDir = CreateTargetDir("dir-filter", b => b
            .AddDirectory(".hidden")
            .AddDirectory("node_modules")
            .AddDirectory("bin")
            .AddDirectory("obj")
            .AddDirectory("packages")
            .AddDirectory("src")
            .AddDirectory("tests")
            .AddFile("dummy.txt", "placeholder"));

        string? capturedPrompt = null;
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedPrompt = callInfo.Arg<AgentInvocation>().Prompt;
                return Ok(JsonSerializer.Serialize(new CompatibilityReport
                {
                    Compatibility = new CompatibilitySection { Overall = "compatible", Score = 100 },
                }));
            });

        CompatibilityAgent sut = CreateSut();
        _ = await sut.AssessAsync(targetDir);

        _ = capturedPrompt.Should().NotBeNull();
        // src and tests should be present
        _ = capturedPrompt.Should().Contain("src");
        _ = capturedPrompt.Should().Contain("tests");
        // excluded dirs should not appear in topLevelDirs
        _ = capturedPrompt.Should().NotContain("node_modules");
    }

    // ── Pre-probe .hone directory detection ─────────────────────────────

    [Fact]
    public async Task CompatibilityAgent_PreProbe_DetectsExistingHoneDir()
    {
        string targetDir = CreateTargetDir("hone-exists", b => b
            .AddFile(".hone/config.yaml", "adapter: dotnet")
            .AddFile(".hone/hooks/build.ps1", "dotnet build")
            .AddFile("MyApp.sln", "solution"));

        string? capturedPrompt = null;
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedPrompt = callInfo.Arg<AgentInvocation>().Prompt;
                return Ok(JsonSerializer.Serialize(new CompatibilityReport
                {
                    Compatibility = new CompatibilitySection { Overall = "compatible", Score = 100 },
                }));
            });

        CompatibilityAgent sut = CreateSut();
        _ = await sut.AssessAsync(targetDir);

        _ = capturedPrompt.Should().NotBeNull();
        _ = capturedPrompt.Should().Contain("\"existingHoneDir\":true");
        _ = capturedPrompt.Should().Contain("config.yaml");
    }

    // ── Pre-probe detects source code paths ─────────────────────────────

    [Fact]
    public async Task CompatibilityAgent_PreProbe_IncludesDetectedSourceCodePaths()
    {
        string targetDir = CreateTargetDir("source-paths", b => b
            .AddFile("MyApp.sln", "solution")
            .AddFile("src/MyApp/MyApp.csproj", "<Project />")
            .AddFile("src/MyApp/Controllers/HomeController.cs", "class HomeController {}")
            .AddFile("src/MyApp/Services/OrderService.cs", "class OrderService {}"));

        string? capturedPrompt = null;
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedPrompt = callInfo.Arg<AgentInvocation>().Prompt;
                return Ok(JsonSerializer.Serialize(new CompatibilityReport
                {
                    Compatibility = new CompatibilitySection { Overall = "compatible", Score = 100 },
                }));
            });

        CompatibilityAgent sut = CreateSut();
        _ = await sut.AssessAsync(targetDir);

        _ = capturedPrompt.Should().NotBeNull();
        _ = capturedPrompt.Should().Contain("detectedSourceCodePaths");
        _ = capturedPrompt.Should().Contain("Source Code Path Detection");
    }

    // ── Report includes detectedConfig with sourceCodePaths ─────────────

    [Fact]
    public async Task CompatibilityAgent_Report_ParsesDetectedConfig()
    {
        string targetDir = CreateTargetDir("detected-config", b => b
            .AddFile("MyApp.sln", "solution"));

        var report = new CompatibilityReport
        {
            Compatibility = new CompatibilitySection
            {
                Overall = "compatible",
                Score = 90,
            },
            DetectedConfig = new DetectedConfigSection
            {
                SourceCodePaths = ["Controllers", "Services", "Data"],
                SourceFileGlob = "*.cs",
                SolutionPath = "MyApp.sln",
                ProjectPath = "src/MyApp",
                TestProjectPath = "tests/MyApp.Tests",
            },
        };

        string json = JsonSerializer.Serialize(report);

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Ok(json));

        CompatibilityAgent sut = CreateSut();
        CompatibilityResult result = await sut.AssessAsync(targetDir);

        _ = result.Success.Should().BeTrue();
        _ = result.Report!.DetectedConfig.Should().NotBeNull();
        _ = result.Report.DetectedConfig!.SourceCodePaths.Should().BeEquivalentTo(
            ["Controllers", "Services", "Data"]);
        _ = result.Report.DetectedConfig.SourceFileGlob.Should().Be("*.cs");
        _ = result.Report.DetectedConfig.ProjectPath.Should().Be("src/MyApp");
    }
}
