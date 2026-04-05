using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Models;

public sealed class HttpReqDurationMetricsTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        HttpReqDurationMetrics original = new(
            Avg: 12.5,
            P50: 10.2,
            P90: 25.1,
            P95: 35.7,
            P99: 85.3,
            Max: 120.5);

        string json = JsonSerializer.Serialize(original);
        HttpReqDurationMetrics? deserialized = JsonSerializer.Deserialize<HttpReqDurationMetrics>(json);

        _ = deserialized.Should().Be(original);
    }

    [Fact]
    public void RoundTrips_WithZeroValues()
    {
        HttpReqDurationMetrics original = new(
            Avg: 0.0,
            P50: 0.0,
            P90: 0.0,
            P95: 0.0,
            P99: 0.0,
            Max: 0.0);

        string json = JsonSerializer.Serialize(original);
        HttpReqDurationMetrics? deserialized = JsonSerializer.Deserialize<HttpReqDurationMetrics>(json);

        _ = deserialized.Should().Be(original);
    }
}
