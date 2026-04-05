using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Hone.Core.Contracts;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Contracts;

public sealed class IAgentRunnerTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void InvokeAsync_ReturnsAgentRunResult()
    {
        MethodInfo? method = typeof(IAgentRunner).GetMethod("InvokeAsync");

        _ = method.Should().NotBeNull("IAgentRunner must define InvokeAsync");
        _ = method!.ReturnType.Should().Be<Task<AgentRunResult>>();
    }

    [Fact]
    public void InvokeAsync_HasExpectedParameters()
    {
        MethodInfo? method = typeof(IAgentRunner).GetMethod("InvokeAsync");
        ParameterInfo[] parameters = method!.GetParameters();

        _ = parameters.Should().HaveCount(2);
        _ = parameters[0].ParameterType.Should().Be<AgentInvocation>();
        _ = parameters[0].Name.Should().Be("invocation");
        _ = parameters[1].ParameterType.Should().Be<CancellationToken>();
        _ = parameters[1].Name.Should().Be("ct");
    }

    [Fact]
    public void AgentInvocation_RoundTrips_ThroughJson()
    {
        AgentInvocation original = new(
            AgentName: "copilot",
            Prompt: "Fix the bug",
            Model: "gpt-4",
            Timeout: TimeSpan.FromMinutes(5),
            WorkingDirectory: "/repo");

        string json = JsonSerializer.Serialize(original);
        AgentInvocation? deserialized = JsonSerializer.Deserialize<AgentInvocation>(json);

        _ = deserialized.Should().Be(original);
    }

    [Fact]
    public void AgentInvocation_RoundTrips_WithNullOptionals()
    {
        AgentInvocation original = new(
            AgentName: "copilot",
            Prompt: "Analyze code");

        string json = JsonSerializer.Serialize(original);
        AgentInvocation? deserialized = JsonSerializer.Deserialize<AgentInvocation>(json);

        _ = deserialized.Should().Be(original);
        _ = deserialized!.Model.Should().BeNull();
        _ = deserialized.Timeout.Should().BeNull();
        _ = deserialized.WorkingDirectory.Should().BeNull();
    }

    [Fact]
    public void AgentRunResult_RoundTrips_ThroughJson()
    {
        AgentRunResult original = new(
            Success: true,
            Output: "Done",
            TimedOut: false,
            ExitCode: 0);

        string json = JsonSerializer.Serialize(original);
        AgentRunResult? deserialized = JsonSerializer.Deserialize<AgentRunResult>(json);

        _ = deserialized.Should().Be(original);
    }

    [Fact]
    public void AgentRunResult_RoundTrips_FailedResult()
    {
        AgentRunResult original = new(
            Success: false,
            Output: "Timeout",
            TimedOut: true,
            ExitCode: -1);

        string json = JsonSerializer.Serialize(original);
        AgentRunResult? deserialized = JsonSerializer.Deserialize<AgentRunResult>(json);

        _ = deserialized.Should().Be(original);
    }
}
