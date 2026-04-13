using FluentAssertions;
using Hone.Agents.Core;
using Hone.Agents.Loop.Critic;
using Hone.Agents.Loop.Implementer;
using Hone.Cli;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using NSubstitute;
using Xunit;

namespace Hone.Integration.Tests;

public sealed class ImplementerPipelineAdapterTests
{
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

        ImplementerPipelineAdapter sut = CreateSut(processRunner);

        string diff = await sut.GetDiffContentAsync("repo", "src/Service.cs", CancellationToken.None);

        _ = diff.Should().BeEmpty();
    }

    private static ImplementerPipelineAdapter CreateSut(IProcessRunner processRunner)
    {
        IAgentRunner runner = Substitute.For<IAgentRunner>();
        AgentInvoker invoker = new(runner, new AgentConfig());

        return new ImplementerPipelineAdapter(
            new ImplementerAgent(invoker),
            new CriticAgent(invoker),
            Substitute.For<IVersionControl>(),
            processRunner,
            Substitute.For<IHoneEventSink>(),
            new HoneConfig());
    }
}
