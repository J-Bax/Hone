using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Models;

public sealed class IterationAttemptTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        IterationAttempt original = new(
            Attempt: 1,
            Stage: "implement",
            Outcome: "success",
            DiffLines: 42);

        string json = JsonSerializer.Serialize(original);
        IterationAttempt? deserialized = JsonSerializer.Deserialize<IterationAttempt>(json);

        _ = deserialized.Should().Be(original);
    }

    [Fact]
    public void RoundTrips_WithZeroDiffLines()
    {
        IterationAttempt original = new(
            Attempt: 3,
            Stage: "verify",
            Outcome: "no-change",
            DiffLines: 0);

        string json = JsonSerializer.Serialize(original);
        IterationAttempt? deserialized = JsonSerializer.Deserialize<IterationAttempt>(json);

        _ = deserialized.Should().Be(original);
    }
}
