using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Models;

public sealed class HttpReqFailedMetricsTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        HttpReqFailedMetrics original = new(Count: 3, Rate: 0.0002);

        string json = JsonSerializer.Serialize(original);
        HttpReqFailedMetrics? deserialized = JsonSerializer.Deserialize<HttpReqFailedMetrics>(json);

        _ = deserialized.Should().Be(original);
    }

    [Fact]
    public void RoundTrips_WithZeroValues()
    {
        HttpReqFailedMetrics original = new(Count: 0, Rate: 0.0);

        string json = JsonSerializer.Serialize(original);
        HttpReqFailedMetrics? deserialized = JsonSerializer.Deserialize<HttpReqFailedMetrics>(json);

        _ = deserialized.Should().Be(original);
    }
}
