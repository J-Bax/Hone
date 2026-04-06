using System.Text.Json;

using FluentAssertions;

using Hone.Agents.Core;
using Hone.Agents.Loop.Classification;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.TestInfrastructure;

using NSubstitute;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Agents.Loop.Tests.Classification;

public sealed class ClassificationAgentTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private readonly IAgentRunner _runner = Substitute.For<IAgentRunner>();

    private ClassificationAgent CreateSut(AgentConfig? config = null)
    {
        AgentInvoker invoker = new(_runner, config ?? new AgentConfig());
        return new ClassificationAgent(invoker);
    }

    private static AgentRunResult OkResult(string json) =>
        new(Success: true, Output: json, TimedOut: false, ExitCode: 0);

    // ── NarrowScope_Detected ────────────────────────────────────────────

    [Fact]
    public async Task ClassificationAgent_NarrowScope_Detected()
    {
        string json = JsonSerializer.Serialize(new { scope = "narrow", reasoning = "Change is contained to a single file" });

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult(json));

        ClassificationAgent sut = CreateSut();

        ClassificationResult result = await sut.ClassifyAsync(
            filePath: "Controllers/ItemsController.cs",
            explanation: "Add response caching",
            targetLabel: "SampleApi",
            workingDirectory: null);

        _ = result.Success.Should().BeTrue();
        _ = result.Scope.Should().Be(OpportunityScope.Narrow);
        _ = result.Reasoning.Should().Be("Change is contained to a single file");
    }

    // ── ArchitectureScope_Detected ──────────────────────────────────────

    [Fact]
    public async Task ClassificationAgent_ArchitectureScope_Detected()
    {
        string json = JsonSerializer.Serialize(new { scope = "architecture", reasoning = "Requires changes across multiple layers" });

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult(json));

        ClassificationAgent sut = CreateSut();

        ClassificationResult result = await sut.ClassifyAsync(
            filePath: "Services/DataService.cs",
            explanation: "Introduce distributed caching",
            targetLabel: "SampleApi",
            workingDirectory: null);

        _ = result.Success.Should().BeTrue();
        _ = result.Scope.Should().Be(OpportunityScope.Architecture);
        _ = result.Reasoning.Should().Be("Requires changes across multiple layers");
    }

    // ── JsonParseFailure_DefaultsToArchitecture ─────────────────────────

    [Fact]
    public async Task ClassificationAgent_JsonParseFailure_ThrowsInvalidOperationException()
    {
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult("This is not valid JSON at all"));

        ClassificationAgent sut = CreateSut();

        Func<Task> act = () => sut.ClassifyAsync(
            filePath: "Controllers/ItemsController.cs",
            explanation: "Optimize endpoint",
            targetLabel: "SampleApi",
            workingDirectory: null);

        _ = await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── UsesCorrectAgentConfig ──────────────────────────────────────────

    [Fact]
    public async Task ClassificationAgent_UsesCorrectAgentConfig()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new { scope = "narrow", reasoning = "test" });

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult(json));

        AgentConfig config = new(ClassificationModel: "custom-classification-model");
        ClassificationAgent sut = CreateSut(config);

        _ = await sut.ClassifyAsync(
            filePath: "Controllers/ItemsController.cs",
            explanation: "Optimize endpoint",
            targetLabel: "SampleApi",
            workingDirectory: null);

        _ = await _runner.Received(1).InvokeAsync(
            Arg.Is<AgentInvocation>(inv =>
                inv.AgentName == "hone-classifier" &&
                inv.Model == "custom-classification-model"),
            Arg.Any<CancellationToken>());
    }

    // ── PromptContainsFileAndExplanation ────────────────────────────────

    [Fact]
    public async Task ClassificationAgent_PromptContainsFileAndExplanation()
    {
        AgentInvocation? capturedInvocation = null;

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedInvocation = callInfo.Arg<AgentInvocation>();
                return OkResult(System.Text.Json.JsonSerializer.Serialize(new { scope = "narrow", reasoning = "test" }));
            });

        ClassificationAgent sut = CreateSut();

        _ = await sut.ClassifyAsync(
            filePath: "Services/AuthService.cs",
            explanation: "JWT validation is synchronous",
            targetLabel: "MyProject",
            workingDirectory: null);

        _ = capturedInvocation.Should().NotBeNull();
        string prompt = capturedInvocation!.Prompt;

        _ = prompt.Should().Contain("Services/AuthService.cs");
        _ = prompt.Should().Contain("JWT validation is synchronous");
        _ = prompt.Should().Contain("MyProject root");
        _ = prompt.Should().Contain("Classify the scope");
        _ = prompt.Should().Contain("Respond with JSON only");
    }
}
