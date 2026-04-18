using FluentAssertions;
using Hone.Agents.Core;
using Hone.Agents.Loop.Critic;
using Hone.Agents.Loop.Implementer;
using Hone.Cli;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Orchestration.Implementer;
using NSubstitute;
using Xunit;

namespace Hone.Integration.Tests;

public sealed class ImplementerPipelineAdapterTests
{
    [Fact]
    public async Task ApplySuggestionAsync_CreatesExperimentBranchBeforeCommit()
    {
        IProcessRunner processRunner = Substitute.For<IProcessRunner>();
        IVersionControl versionControl = Substitute.For<IVersionControl>();
        ImplementerPipelineAdapter sut = CreateSut(
            processRunner: processRunner,
            versionControl: versionControl);
        string tempDir = CreateTargetDir();

        try
        {
            _ = versionControl.GetCurrentBranchAsync(tempDir, Arg.Any<CancellationToken>())
                .Returns("master");
            _ = processRunner.RunAsync(
                    "git",
                    Arg.Any<IEnumerable<string>>(),
                    tempDir,
                    Arg.Any<TimeSpan?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ProcessResult(
                    Success: false,
                    Output: string.Empty,
                    ExitCode: 1,
                    TimedOut: false)));

            ApplyStepResult result = await sut.ApplySuggestionAsync(
                new ApplyStepInput(
                    FilePath: "src\\Service.cs",
                    NewContent: "updated content",
                    Description: "optimize hot path",
                    Experiment: 2,
                    BaseBranch: "master",
                    TargetDir: tempDir),
                CancellationToken.None);

            _ = result.Success.Should().BeTrue();
            await versionControl.Received(1).CheckoutAsync(
                tempDir, "hone/experiment-2", create: true, Arg.Any<CancellationToken>());
            await versionControl.Received(1).CommitAsync(
                tempDir,
                "hone(experiment-2): optimize hot path",
                Arg.Is<IEnumerable<string>>(paths => paths.Contains("src\\Service.cs")),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ApplySuggestionAsync_ReusesCurrentExperimentBranchDuringRetry()
    {
        IProcessRunner processRunner = Substitute.For<IProcessRunner>();
        IVersionControl versionControl = Substitute.For<IVersionControl>();
        string tempDir = CreateTargetDir();
        _ = versionControl.GetCurrentBranchAsync(tempDir, Arg.Any<CancellationToken>())
            .Returns("hone/experiment-2");

        ImplementerPipelineAdapter sut = CreateSut(
            processRunner: processRunner,
            versionControl: versionControl);

        try
        {
            ApplyStepResult result = await sut.ApplySuggestionAsync(
                new ApplyStepInput(
                    FilePath: "src\\Service.cs",
                    NewContent: "updated content",
                    Description: "retry optimization",
                    Experiment: 2,
                    BaseBranch: "master",
                    TargetDir: tempDir),
                CancellationToken.None);

            _ = result.Success.Should().BeTrue();
            await versionControl.DidNotReceive().CheckoutAsync(
                tempDir, "master", create: false, Arg.Any<CancellationToken>());
            await versionControl.DidNotReceive().CheckoutAsync(
                tempDir, "hone/experiment-2", create: true, Arg.Any<CancellationToken>());
            await versionControl.Received(1).CommitAsync(
                tempDir,
                "hone(experiment-2): retry optimization",
                Arg.Is<IEnumerable<string>>(paths => paths.Contains("src\\Service.cs")),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetDiffContentAsync_WhenGitDiffFails_ReturnsEmptyString()
    {
        IProcessRunner processRunner = Substitute.For<IProcessRunner>();
        _ = processRunner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<string?>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessResult(
                Success: false,
                Output: "fatal: bad revision 'HEAD~1'",
                ExitCode: 128,
                TimedOut: false)));

        ImplementerPipelineAdapter sut = CreateSut(processRunner: processRunner);

        string diff = await sut.GetDiffContentAsync("repo", "src/Service.cs", CancellationToken.None);

        _ = diff.Should().BeEmpty();
    }

    private static ImplementerPipelineAdapter CreateSut(
        IProcessRunner processRunner,
        IVersionControl? versionControl = null,
        HoneConfig? config = null)
    {
        IAgentRunner runner = Substitute.For<IAgentRunner>();
        AgentInvoker invoker = new(runner, new AgentConfig());

        return new ImplementerPipelineAdapter(
            new ImplementerAgent(invoker),
            new CriticAgent(invoker),
            versionControl ?? Substitute.For<IVersionControl>(),
            processRunner,
            Substitute.For<IHoneEventSink>(),
            config ?? new HoneConfig());
    }

    private static string CreateTargetDir()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _ = Directory.CreateDirectory(Path.Combine(tempDir, "src"));
        return tempDir;
    }
}
