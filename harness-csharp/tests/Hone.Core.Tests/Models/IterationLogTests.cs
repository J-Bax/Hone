using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Models;

public sealed class IterationLogTests(ITestOutputHelper output) : HoneTestBase(output)
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
    public void Attempts_DefaultsToEmptyList()
    {
        IterationLog log = new(Attempts: null!);
        _ = log.Attempts.Should().BeEmpty();
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
