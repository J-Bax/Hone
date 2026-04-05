using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Xunit;

namespace Hone.Core.Tests.Models;

public sealed class IterativeFixResultTests
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        IterativeFixResult original = new(
            Success: true,
            AttemptCount: 3,
            ExitReason: "tests-pass",
            FailureDetail: null,
            IterationLog: new IterationLog(
            [
                new IterationAttempt(1, "implement", "build-fail", 50),
                new IterationAttempt(2, "implement", "test-fail", 45),
                new IterationAttempt(3, "implement", "success", 42),
            ]),
            IterationLogRelativePath: "logs/iteration-log.json");

        string json = JsonSerializer.Serialize(original);
        IterativeFixResult? deserialized = JsonSerializer.Deserialize<IterativeFixResult>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Success.Should().BeTrue();
        _ = deserialized.AttemptCount.Should().Be(3);
        _ = deserialized.ExitReason.Should().Be("tests-pass");
        _ = deserialized.FailureDetail.Should().BeNull();
        _ = deserialized.IterationLog.Should().NotBeNull();
        _ = deserialized.IterationLog!.Attempts.Should().HaveCount(3);
        _ = deserialized.IterationLog.Attempts[0].Should().Be(original.IterationLog!.Attempts[0]);
        _ = deserialized.IterationLogRelativePath.Should().Be("logs/iteration-log.json");
    }

    [Fact]
    public void RoundTrips_FailedResult()
    {
        IterativeFixResult original = new(
            Success: false,
            AttemptCount: 5,
            ExitReason: "max-attempts",
            FailureDetail: "Tests still failing after 5 attempts",
            IterationLog: null,
            IterationLogRelativePath: null);

        string json = JsonSerializer.Serialize(original);
        IterativeFixResult? deserialized = JsonSerializer.Deserialize<IterativeFixResult>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Success.Should().BeFalse();
        _ = deserialized.AttemptCount.Should().Be(5);
        _ = deserialized.ExitReason.Should().Be("max-attempts");
        _ = deserialized.FailureDetail.Should().Be("Tests still failing after 5 attempts");
        _ = deserialized.IterationLog.Should().BeNull();
        _ = deserialized.IterationLogRelativePath.Should().BeNull();
    }
}
