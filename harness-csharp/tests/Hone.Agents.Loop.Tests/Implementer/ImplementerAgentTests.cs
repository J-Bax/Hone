using FluentAssertions;

using Hone.Agents.Core;
using Hone.Agents.Loop.Implementer;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.TestInfrastructure;

using NSubstitute;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Agents.Loop.Tests.Implementer;

public sealed class ImplementerAgentTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private readonly IAgentRunner _runner = Substitute.For<IAgentRunner>();

    private ImplementerAgent CreateSut(AgentConfig? config = null)
    {
        AgentInvoker invoker = new(_runner, config ?? new AgentConfig());
        return new ImplementerAgent(invoker);
    }

    private static AgentRunResult OkResult(string text) =>
        new(Success: true, Output: text, TimedOut: false, ExitCode: 0);

    // ── ExtractsCodeBlock ───────────────────────────────────────────────

    [Fact]
    public async Task ImplementerAgent_ExtractsCodeBlock()
    {
        const string Code = "using System;\n\npublic class Foo { }";
        string response = $"```csharp\n{Code}\n```";

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult(response));

        ImplementerAgent sut = CreateSut();

        ImplementerResult result = await sut.ImplementAsync(
            filePath: "Controllers/ItemsController.cs",
            explanation: "Add response caching",
            targetLabel: "SampleApi",
            workingDirectory: null);

        _ = result.Success.Should().BeTrue();
        _ = result.CodeBlock.Should().Be(Code);
        _ = result.Attempt.Should().Be(1);
    }

    // ── IncludesRCA ─────────────────────────────────────────────────────

    [Fact]
    public async Task ImplementerAgent_IncludesRCA()
    {
        AgentInvocation? capturedInvocation = null;

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedInvocation = callInfo.Arg<AgentInvocation>();
                return OkResult("```csharp\ncode\n```");
            });

        ImplementerAgent sut = CreateSut();

        _ = await sut.ImplementAsync(
            filePath: "Services/DataService.cs",
            explanation: "Optimize query",
            targetLabel: "SampleApi",
            workingDirectory: null,
            rootCauseDocument: "## Evidence\nThe N+1 query pattern causes 200ms latency.");

        _ = capturedInvocation.Should().NotBeNull();
        string prompt = capturedInvocation!.Prompt;

        _ = prompt.Should().Contain("## Root Cause Analysis");
        _ = prompt.Should().Contain("The N+1 query pattern causes 200ms latency.");
    }

    // ── RetryIncludesPreviousErrors ─────────────────────────────────────

    [Fact]
    public async Task ImplementerAgent_RetryIncludesPreviousErrors()
    {
        AgentInvocation? capturedInvocation = null;

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedInvocation = callInfo.Arg<AgentInvocation>();
                return OkResult("```csharp\nfixed code\n```");
            });

        ImplementerAgent sut = CreateSut();

        ImplementerResult result = await sut.ImplementAsync(
            filePath: "Controllers/ItemsController.cs",
            explanation: "Add caching",
            targetLabel: "SampleApi",
            workingDirectory: null,
            attempt: 2,
            previousErrors: "CS1002: ; expected",
            currentFileContent: "public class Broken {");

        _ = capturedInvocation.Should().NotBeNull();
        string prompt = capturedInvocation!.Prompt;

        _ = prompt.Should().Contain("## Retry Context");
        _ = prompt.Should().Contain("attempt 2");
        _ = prompt.Should().Contain("### Error Output");
        _ = prompt.Should().Contain("CS1002: ; expected");
        _ = prompt.Should().Contain("### Current File Content (failed attempt)");
        _ = prompt.Should().Contain("public class Broken {");
        _ = prompt.Should().Contain("Fix the failure above");
        _ = result.Attempt.Should().Be(2);
    }

    // ── NoCodeBlock_ReturnsFailure ──────────────────────────────────────

    [Fact]
    public async Task ImplementerAgent_NoCodeBlock_ReturnsFailure()
    {
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult("I couldn't make the change because the file doesn't exist."));

        ImplementerAgent sut = CreateSut();

        ImplementerResult result = await sut.ImplementAsync(
            filePath: "NonExistent.cs",
            explanation: "Optimize something",
            targetLabel: "SampleApi",
            workingDirectory: null);

        _ = result.Success.Should().BeFalse();
        _ = result.CodeBlock.Should().BeNull();
        _ = result.Response.Should().Contain("couldn't make the change");
    }

    // ── UsesFixModelConfig ──────────────────────────────────────────────

    [Fact]
    public async Task ImplementerAgent_UsesFixModelConfig()
    {
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult("```\ncode\n```"));

        AgentConfig config = new(ImplementerModel: "custom-fixer-model");
        ImplementerAgent sut = CreateSut(config);

        _ = await sut.ImplementAsync(
            filePath: "Controllers/ItemsController.cs",
            explanation: "Optimize endpoint",
            targetLabel: "SampleApi",
            workingDirectory: null);

        _ = await _runner.Received(1).InvokeAsync(
            Arg.Is<AgentInvocation>(inv =>
                inv.AgentName == "hone-fixer" &&
                inv.Model == "custom-fixer-model"),
            Arg.Any<CancellationToken>());
    }
}
