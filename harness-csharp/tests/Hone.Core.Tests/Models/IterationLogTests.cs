using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Xunit;

namespace Hone.Core.Tests.Models;

public sealed class IterationLogTests
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        IterationLog original = new(
            Attempts:
            [
                new IterationAttempt(1, "implement", "success", 42),
                new IterationAttempt(2, "verify", "failure", 0),
                new IterationAttempt(3, "implement", "success", 15),
            ]);

        string json = JsonSerializer.Serialize(original);
        IterationLog? deserialized = JsonSerializer.Deserialize<IterationLog>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Attempts.Should().HaveCount(3);
        _ = deserialized.Attempts[0].Should().Be(original.Attempts[0]);
        _ = deserialized.Attempts[1].Should().Be(original.Attempts[1]);
        _ = deserialized.Attempts[2].Should().Be(original.Attempts[2]);
    }

    [Fact]
    public void RoundTrips_WithEmptyAttempts()
    {
        IterationLog original = new(Attempts: []);

        string json = JsonSerializer.Serialize(original);
        IterationLog? deserialized = JsonSerializer.Deserialize<IterationLog>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Attempts.Should().BeEmpty();
    }
}
