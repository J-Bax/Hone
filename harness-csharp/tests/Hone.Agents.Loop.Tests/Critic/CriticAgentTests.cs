using System.Text.Json;

using FluentAssertions;

using Hone.Agents.Core;
using Hone.Agents.Loop.Critic;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.TestInfrastructure;

using NSubstitute;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Agents.Loop.Tests.Critic;

public sealed class CriticAgentTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private readonly IAgentRunner _runner = Substitute.For<IAgentRunner>();

    private CriticAgent CreateSut(AgentConfig? config = null)
    {
        AgentInvoker invoker = new(_runner, config ?? new AgentConfig());
        return new CriticAgent(invoker);
    }

    private static AgentRunResult OkResult(string json) =>
        new(Success: true, Output: json, TimedOut: false, ExitCode: 0);

    // ── Approve_ReturnsApproved ─────────────────────────────────────────

    [Fact]
    public async Task ReviewAsync_Approve_ReturnsApproved()
    {
        string json = JsonSerializer.Serialize(new
        {
            verdict = "approve",
            confidence = "high",
            issues = Array.Empty<object>(),
            summary = "Change looks correct and well-scoped.",
        });

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult(json));

        CriticAgent sut = CreateSut();

        CriticResult result = await sut.ReviewAsync(
            filePath: "Controllers/ItemsController.cs",
            explanation: "Add response caching",
            diff: "+ [ResponseCache(Duration = 60)]",
            classificationScope: "NARROW",
            targetLabel: "SampleApi",
            workingDirectory: null);

        _ = result.Success.Should().BeTrue();
        _ = result.Approved.Should().BeTrue();
        _ = result.Verdict.Should().Be("APPROVE");
        _ = result.Confidence.Should().Be("high");
        _ = result.Summary.Should().Be("Change looks correct and well-scoped.");
        _ = result.Issues.Should().NotBeNull().And.BeEmpty();
        _ = result.Feedback.Should().BeNull();
    }

    // ── Reject_ReturnsFeedback ──────────────────────────────────────────

    [Fact]
    public async Task ReviewAsync_Reject_ReturnsFeedback()
    {
        string json = JsonSerializer.Serialize(new
        {
            verdict = "reject",
            confidence = "high",
            issues = new[]
            {
                new
                {
                    severity = "blocking",
                    category = "correctness",
                    description = "Cache lacks invalidation on write path",
                    suggestion = "Add cache eviction in the POST handler",
                },
            },
            summary = "Missing cache invalidation will cause stale data under load.",
        });

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult(json));

        CriticAgent sut = CreateSut();

        CriticResult result = await sut.ReviewAsync(
            filePath: "Controllers/ItemsController.cs",
            explanation: "Add response caching",
            diff: "+ [ResponseCache(Duration = 60)]",
            classificationScope: "NARROW",
            targetLabel: "SampleApi",
            workingDirectory: null);

        _ = result.Success.Should().BeTrue();
        _ = result.Approved.Should().BeFalse();
        _ = result.Verdict.Should().Be("REJECT");
        _ = result.Issues.Should().HaveCount(1);
        _ = result.Feedback.Should().Contain("Cache lacks invalidation");
        _ = result.Feedback.Should().Contain("Add cache eviction");
    }

    // ── NonBlockingIssues_StillApproves ──────────────────────────────────

    [Fact]
    public async Task ReviewAsync_WarningIssues_StillApproves()
    {
        string json = JsonSerializer.Serialize(new
        {
            verdict = "approve",
            confidence = "medium",
            issues = new[]
            {
                new
                {
                    severity = "warning",
                    category = "quality",
                    description = "Consider extracting magic number to constant",
                    suggestion = "Define const int CacheDurationSec = 60",
                },
            },
            summary = "Approved with minor suggestions.",
        });

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult(json));

        CriticAgent sut = CreateSut();

        CriticResult result = await sut.ReviewAsync(
            filePath: "Controllers/ItemsController.cs",
            explanation: "Add response caching",
            diff: "+ [ResponseCache(Duration = 60)]",
            classificationScope: null,
            targetLabel: "SampleApi",
            workingDirectory: null);

        _ = result.Approved.Should().BeTrue();
        _ = result.Issues.Should().HaveCount(1);
        _ = result.Feedback.Should().BeNull("warning issues should not produce feedback");
    }

    // ── AgentFailure_ReturnsNotApproved ──────────────────────────────────

    [Fact]
    public async Task ReviewAsync_AgentFailure_ReturnsNotApproved()
    {
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult("This is not valid JSON at all"));

        CriticAgent sut = CreateSut();

        CriticResult result = await sut.ReviewAsync(
            filePath: "Controllers/ItemsController.cs",
            explanation: "Optimize endpoint",
            diff: "+ some change",
            classificationScope: "NARROW",
            targetLabel: "SampleApi",
            workingDirectory: null);

        _ = result.Success.Should().BeFalse();
        _ = result.Approved.Should().BeFalse();
    }

    // ── UsesCorrectAgentConfig ──────────────────────────────────────────

    [Fact]
    public async Task ReviewAsync_UsesCorrectModelConfig()
    {
        string json = JsonSerializer.Serialize(new
        {
            verdict = "approve",
            confidence = "high",
            issues = Array.Empty<object>(),
            summary = "OK",
        });

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult(json));

        AgentConfig config = new(CriticModel: "custom-critic-model");
        CriticAgent sut = CreateSut(config);

        _ = await sut.ReviewAsync(
            filePath: "Controllers/ItemsController.cs",
            explanation: "Optimize endpoint",
            diff: "+ some change",
            classificationScope: "NARROW",
            targetLabel: "SampleApi",
            workingDirectory: null);

        _ = await _runner.Received(1).InvokeAsync(
            Arg.Is<AgentInvocation>(inv =>
                inv.AgentName == "hone-critic" &&
                inv.Model == "custom-critic-model"),
            Arg.Any<CancellationToken>());
    }

    // ── PromptContainsAllContext ─────────────────────────────────────────

    [Fact]
    public async Task ReviewAsync_PromptContainsAllContext()
    {
        AgentInvocation? capturedInvocation = null;

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedInvocation = callInfo.Arg<AgentInvocation>();
                return OkResult(JsonSerializer.Serialize(new
                {
                    verdict = "approve",
                    confidence = "high",
                    issues = Array.Empty<object>(),
                    summary = "OK",
                }));
            });

        CriticAgent sut = CreateSut();

        _ = await sut.ReviewAsync(
            filePath: "Services/CacheService.cs",
            explanation: "Add distributed caching",
            diff: "+ services.AddStackExchangeRedisCache()",
            classificationScope: "NARROW",
            targetLabel: "MyProject",
            workingDirectory: null);

        _ = capturedInvocation.Should().NotBeNull();
        string prompt = capturedInvocation!.Prompt;

        _ = prompt.Should().Contain("Services/CacheService.cs");
        _ = prompt.Should().Contain("Add distributed caching");
        _ = prompt.Should().Contain("AddStackExchangeRedisCache");
        _ = prompt.Should().Contain("NARROW");
        _ = prompt.Should().Contain("MyProject");
        _ = prompt.Should().Contain("Respond with JSON only");
    }

    // ── MissingVerdict_DefaultsToReject ──────────────────────────────────

    [Fact]
    public async Task ReviewAsync_MissingVerdict_DefaultsToReject()
    {
        string json = JsonSerializer.Serialize(new
        {
            confidence = "low",
            issues = Array.Empty<object>(),
            summary = "Uncertain.",
        });

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult(json));

        CriticAgent sut = CreateSut();

        CriticResult result = await sut.ReviewAsync(
            filePath: "Controllers/ItemsController.cs",
            explanation: "Optimize endpoint",
            diff: "+ some change",
            classificationScope: "NARROW",
            targetLabel: "SampleApi",
            workingDirectory: null);

        _ = result.Success.Should().BeTrue();
        _ = result.Approved.Should().BeFalse("missing verdict should default to reject");
        _ = result.Verdict.Should().Be("REJECT");
    }
}
