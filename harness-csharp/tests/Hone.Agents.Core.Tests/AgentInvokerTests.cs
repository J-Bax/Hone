using System.Text.Json;

using FluentAssertions;

using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.TestInfrastructure;

using NSubstitute;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Agents.Core.Tests;

public sealed class AgentInvokerTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private readonly IAgentRunner _runner = Substitute.For<IAgentRunner>();

    private static AgentRunResult Ok(string output = "{}") =>
        new(Success: true, Output: output, TimedOut: false, ExitCode: 0);

    private AgentInvoker CreateSut(AgentConfig? config = null) =>
        new(_runner, config ?? new AgentConfig());

    // ── Model resolution ────────────────────────────────────────────────

    [Fact]
    public async Task AgentInvoker_ModelResolution_PerAgentOverride()
    {
        var config = new AgentConfig(
            DefaultModel: "default-model",
            AnalysisModel: "analysis-model-override");
        AgentInvoker sut = CreateSut(config);

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Ok());

        var options = new AgentInvocationOptions(
            AgentName: "test-agent",
            Prompt: "test prompt",
            ModelConfigKey: "AnalysisModel");

        _ = await sut.InvokeAgentAsync<object>(options);

        _ = await _runner.Received(1).InvokeAsync(
            Arg.Is<AgentInvocation>(inv => inv.Model == "analysis-model-override"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AgentInvoker_ModelResolution_FallsBackToDefaultModel()
    {
        AgentInvoker sut = CreateSut();

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Ok());

        var options = new AgentInvocationOptions(
            AgentName: "test-agent",
            Prompt: "test prompt",
            DefaultModel: "my-fallback-model");

        _ = await sut.InvokeAgentAsync<object>(options);

        _ = await _runner.Received(1).InvokeAsync(
            Arg.Is<AgentInvocation>(inv => inv.Model == "my-fallback-model"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AgentInvoker_ModelResolution_FallsBackToConfigDefault()
    {
        var config = new AgentConfig(DefaultModel: "config-default");
        AgentInvoker sut = CreateSut(config);

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Ok());

        var options = new AgentInvocationOptions(
            AgentName: "test-agent",
            Prompt: "test prompt");

        _ = await sut.InvokeAgentAsync<object>(options);

        _ = await _runner.Received(1).InvokeAsync(
            Arg.Is<AgentInvocation>(inv => inv.Model == "config-default"),
            Arg.Any<CancellationToken>());
    }

    // ── JSON extraction / sanitization ──────────────────────────────────

    [Fact]
    public async Task AgentInvoker_MarkdownFences_Stripped()
    {
        string fencedJson = "```json\n{\"value\": 42}\n```";

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Ok(fencedJson));

        AgentInvoker sut = CreateSut();
        var options = new AgentInvocationOptions(AgentName: "test", Prompt: "go");

        AgentResult<SimpleDto> result = await sut.InvokeAgentAsync<SimpleDto>(options);

        _ = result.Success.Should().BeTrue();
        _ = result.ParsedResult.Should().NotBeNull();
        _ = result.ParsedResult!.Value.Should().Be(42);
    }

    [Fact]
    public async Task AgentInvoker_PrefixedChatterWithBraceNoise_ParsesLargestValidJson()
    {
        string agentOutput =
            """
            Let me verify the target.
            Example remediation: Build: { Type: BuiltIn, Name: dotnet-build }
            Final answer:
            {"value": 42, "name": "parsed"}
            """;

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Ok(agentOutput));

        AgentInvoker sut = CreateSut();
        var options = new AgentInvocationOptions(AgentName: "test", Prompt: "go");

        AgentResult<SimpleDto> result = await sut.InvokeAgentAsync<SimpleDto>(options);

        _ = result.Success.Should().BeTrue();
        _ = result.ParsedResult.Should().NotBeNull();
        _ = result.ParsedResult!.Value.Should().Be(42);
        _ = result.ParsedResult.Name.Should().Be("parsed");
    }

    [Fact]
    public async Task AgentInvoker_JsonParseFailed_RetriesWithSanitization()
    {
        string nanJson = "{\"value\": NaN}";
        string validJson = "{\"value\": 10}";

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(
                Ok(nanJson),
                Ok(validJson));

        AgentInvoker sut = CreateSut();
        var options = new AgentInvocationOptions(
            AgentName: "test",
            Prompt: "go",
            MaxRetries: 1,
            RetryPromptSuffix: "Please return valid JSON.");

        AgentResult<SimpleDto> result = await sut.InvokeAgentAsync<SimpleDto>(options);

        _ = result.Success.Should().BeTrue();
        _ = result.ParsedResult.Should().NotBeNull();
        _ = result.ParsedResult!.Value.Should().Be(10);

        _ = await _runner.Received(2).InvokeAsync(
            Arg.Any<AgentInvocation>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AgentInvoker_Timeout_ReturnsFailure()
    {
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult(
                Success: false, Output: "timed out", TimedOut: true, ExitCode: -1));

        AgentInvoker sut = CreateSut();
        var options = new AgentInvocationOptions(AgentName: "test", Prompt: "go");

        AgentResult<SimpleDto> result = await sut.InvokeAgentAsync<SimpleDto>(options);

        _ = result.Success.Should().BeFalse();
        _ = result.TimedOut.Should().BeTrue();
        _ = result.ParsedResult.Should().BeNull();
    }

    [Fact]
    public async Task AgentInvoker_SuccessfulParse_ReturnsTypedResult()
    {
        string json = JsonSerializer.Serialize(new SimpleDto { Value = 99, Name = "hello" });

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Ok(json));

        AgentInvoker sut = CreateSut();
        var options = new AgentInvocationOptions(AgentName: "test", Prompt: "go");

        AgentResult<SimpleDto> result = await sut.InvokeAgentAsync<SimpleDto>(options);

        _ = result.Success.Should().BeTrue();
        _ = result.ParsedResult.Should().NotBeNull();
        _ = result.ParsedResult!.Value.Should().Be(99);
        _ = result.ParsedResult.Name.Should().Be("hello");
        _ = result.ExitCode.Should().Be(0);
        _ = result.TimedOut.Should().BeFalse();
    }

    [Fact]
    public async Task AgentInvoker_AllRetriesExhausted_ReturnsLastResponse()
    {
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Ok("not json at all"));

        AgentInvoker sut = CreateSut();
        var options = new AgentInvocationOptions(
            AgentName: "test",
            Prompt: "go",
            MaxRetries: 2,
            RetryPromptSuffix: "Try again.");

        AgentResult<SimpleDto> result = await sut.InvokeAgentAsync<SimpleDto>(options);

        _ = await _runner.Received(3).InvokeAsync(
            Arg.Any<AgentInvocation>(),
            Arg.Any<CancellationToken>());

        _ = result.ParsedResult.Should().BeNull();
        _ = result.RawOutput.Should().Be("not json at all");
        _ = result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task AgentInvoker_RunnerThrows_ReturnsFailure()
    {
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns<AgentRunResult>(_ => throw new InvalidOperationException("runner broke"));

        AgentInvoker sut = CreateSut();
        var options = new AgentInvocationOptions(AgentName: "test", Prompt: "go");

        AgentResult<SimpleDto> result = await sut.InvokeAgentAsync<SimpleDto>(options);

        _ = result.Success.Should().BeFalse();
        _ = result.ExitCode.Should().Be(-1);
        _ = result.RawOutput.Should().Contain("runner broke");
    }

    [Fact]
    public async Task AgentInvoker_RetryPromptSuffix_AppendedOnRetry()
    {
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(
                Ok("bad json"),
                Ok("{\"value\": 1}"));

        AgentInvoker sut = CreateSut();
        var options = new AgentInvocationOptions(
            AgentName: "test",
            Prompt: "original prompt",
            MaxRetries: 1,
            RetryPromptSuffix: "RETRY SUFFIX");

        _ = await sut.InvokeAgentAsync<SimpleDto>(options);

        Received.InOrder(() =>
        {
            _ = _runner.InvokeAsync(
                Arg.Is<AgentInvocation>(inv => inv.Prompt == "original prompt"),
                Arg.Any<CancellationToken>());
            _ = _runner.InvokeAsync(
                Arg.Is<AgentInvocation>(inv =>
                    inv.Prompt.Contains("RETRY SUFFIX", StringComparison.Ordinal)
                    && !inv.Prompt.Contains("bad json", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task AgentInvoker_RetryPrompt_CanIncludePreviousInvalidOutput()
    {
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(
                Ok("bad json"),
                Ok("{\"value\": 1}"));

        AgentInvoker sut = CreateSut();
        var options = new AgentInvocationOptions(
            AgentName: "test",
            Prompt: "original prompt",
            MaxRetries: 1,
            RetryPromptSuffix: "RETRY SUFFIX",
            IncludePreviousOutputInRetryPrompt: true);

        _ = await sut.InvokeAgentAsync<SimpleDto>(options);

        Received.InOrder(() =>
        {
            _ = _runner.InvokeAsync(
                Arg.Is<AgentInvocation>(inv => inv.Prompt == "original prompt"),
                Arg.Any<CancellationToken>());
            _ = _runner.InvokeAsync(
                Arg.Is<AgentInvocation>(inv =>
                    inv.Prompt.Contains("RETRY SUFFIX", StringComparison.Ordinal)
                    && inv.Prompt.Contains("Previous Invalid Response To Repair", StringComparison.Ordinal)
                    && inv.Prompt.Contains("bad json", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task AgentInvoker_RetryPrompt_EscapesTripleBackticksInPreviousOutput()
    {
        const string PreviousOutput =
            """
            before
            ```
            after
            """;

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(
                Ok(PreviousOutput),
                Ok("{\"value\": 1}"));

        AgentInvoker sut = CreateSut();
        var options = new AgentInvocationOptions(
            AgentName: "test",
            Prompt: "original prompt",
            MaxRetries: 1,
            RetryPromptSuffix: "RETRY SUFFIX",
            IncludePreviousOutputInRetryPrompt: true);

        _ = await sut.InvokeAgentAsync<SimpleDto>(options);

        _ = await _runner.Received(1).InvokeAsync(
            Arg.Is<AgentInvocation>(inv =>
                inv.Prompt.Contains("Previous Invalid Response To Repair", StringComparison.Ordinal)
                && inv.Prompt.Contains("``\\`", StringComparison.Ordinal)
                && !inv.Prompt.Contains(PreviousOutput, StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AgentInvoker_TimeoutPropagated_FromAgentConfig()
    {
        var config = new AgentConfig(AgentTimeoutSec: 300);
        AgentInvoker sut = CreateSut(config);

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(Ok());

        var options = new AgentInvocationOptions(AgentName: "test", Prompt: "go");

        _ = await sut.InvokeAgentAsync<object>(options);

        _ = await _runner.Received(1).InvokeAsync(
            Arg.Is<AgentInvocation>(inv => inv.Timeout == TimeSpan.FromSeconds(300)),
            Arg.Any<CancellationToken>());
    }

    // ── Test DTO ────────────────────────────────────────────────────────

    private sealed class SimpleDto
    {
        public int Value { get; set; }

        public string? Name { get; set; }
    }
}
