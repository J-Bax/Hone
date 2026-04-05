using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Models;

public sealed class HttpReqCountMetricsTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        HttpReqCountMetrics original = new(Count: 15000, Rate: 125.5);

        string json = JsonSerializer.Serialize(original);
        HttpReqCountMetrics? deserialized = JsonSerializer.Deserialize<HttpReqCountMetrics>(json);

        _ = deserialized.Should().Be(original);
    }

    [Fact]
    public void RoundTrips_WithZeroValues()
    {
        HttpReqCountMetrics original = new(Count: 0, Rate: 0.0);

        string json = JsonSerializer.Serialize(original);
        HttpReqCountMetrics? deserialized = JsonSerializer.Deserialize<HttpReqCountMetrics>(json);

        _ = deserialized.Should().Be(original);
    }
}
