using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Models;

public sealed class ProcessResultTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        ProcessResult original = new(
            Success: true,
            Output: "Build succeeded.",
            ExitCode: 0,
            TimedOut: false);

        string json = JsonSerializer.Serialize(original);
        ProcessResult? deserialized = JsonSerializer.Deserialize<ProcessResult>(json);

        _ = deserialized.Should().Be(original);
    }

    [Fact]
    public void RoundTrips_FailedResult()
    {
        ProcessResult original = new(
            Success: false,
            Output: "Timeout after 60s",
            ExitCode: -1,
            TimedOut: true);

        string json = JsonSerializer.Serialize(original);
        ProcessResult? deserialized = JsonSerializer.Deserialize<ProcessResult>(json);

        _ = deserialized.Should().Be(original);
    }
}
