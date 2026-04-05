using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Models;

public sealed class MetricComparisonTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        MetricComparison original = new(
            MetricName: "p95",
            Current: 35.7,
            Previous: 42.1,
            Baseline: 40.0,
            DeltaPct: -15.2,
            AbsoluteDelta: -6.4,
            Improved: true,
            Regressed: false);

        string json = JsonSerializer.Serialize(original);
        MetricComparison? deserialized = JsonSerializer.Deserialize<MetricComparison>(json);

        _ = deserialized.Should().Be(original);
    }

    [Fact]
    public void RoundTrips_WithNullBaseline()
    {
        MetricComparison original = new(
            MetricName: "rps",
            Current: 125.5,
            Previous: 120.0,
            Baseline: null,
            DeltaPct: 4.6,
            AbsoluteDelta: 5.5,
            Improved: true,
            Regressed: false);

        string json = JsonSerializer.Serialize(original);
        MetricComparison? deserialized = JsonSerializer.Deserialize<MetricComparison>(json);

        _ = deserialized.Should().Be(original);
    }
}
