using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Xunit;

namespace Hone.Core.Tests.Models;

public sealed class MetricSetTests
{
    [Fact]
    public void MetricSet_Serialization_RoundTrips()
    {
        MetricSet original = CreateSampleMetricSet();

        string json = JsonSerializer.Serialize(original);
        MetricSet? deserialized = JsonSerializer.Deserialize<MetricSet>(json);

        _ = deserialized.Should().Be(original);
    }

    [Fact]
    public void MetricSet_FromK6Summary_ParsesAllFields()
    {
        // A MetricSet populated with values matching a typical k6 summary output.
        const string MetricSetJson = """
            {
                "Timestamp": "2024-01-15T10:30:00Z",
                "Experiment": 1,
                "Run": 1,
                "HttpReqDuration": {
                    "Avg": 12.5,
                    "P50": 10.2,
                    "P90": 25.1,
                    "P95": 35.7,
                    "P99": 85.3,
                    "Max": 120.5
                },
                "HttpReqs": {
                    "Count": 15000,
                    "Rate": 125.5
                },
                "HttpReqFailed": {
                    "Count": 3,
                    "Rate": 0.0002
                },
                "SummaryPath": "results/k6-summary.json"
            }
            """;

        MetricSet? deserialized = JsonSerializer.Deserialize<MetricSet>(MetricSetJson);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Timestamp.Should().Be("2024-01-15T10:30:00Z");
        _ = deserialized.Experiment.Should().Be(1);
        _ = deserialized.Run.Should().Be(1);
        _ = deserialized.HttpReqDuration.Avg.Should().Be(12.5);
        _ = deserialized.HttpReqDuration.P50.Should().Be(10.2);
        _ = deserialized.HttpReqDuration.P90.Should().Be(25.1);
        _ = deserialized.HttpReqDuration.P95.Should().Be(35.7);
        _ = deserialized.HttpReqDuration.P99.Should().Be(85.3);
        _ = deserialized.HttpReqDuration.Max.Should().Be(120.5);
        _ = deserialized.HttpReqs.Count.Should().Be(15000);
        _ = deserialized.HttpReqs.Rate.Should().Be(125.5);
        _ = deserialized.HttpReqFailed.Count.Should().Be(3);
        _ = deserialized.HttpReqFailed.Rate.Should().Be(0.0002);
        _ = deserialized.SummaryPath.Should().Be("results/k6-summary.json");
    }

    [Fact]
    public void RoundTrips_WithNullSummaryPath()
    {
        MetricSet original = CreateSampleMetricSet() with { SummaryPath = null };

        string json = JsonSerializer.Serialize(original);
        MetricSet? deserialized = JsonSerializer.Deserialize<MetricSet>(json);

        _ = deserialized.Should().Be(original);
    }

    private static MetricSet CreateSampleMetricSet() =>
        new(
            Timestamp: "2024-01-15T10:30:00Z",
            Experiment: 1,
            Run: 1,
            HttpReqDuration: new(Avg: 12.5, P50: 10.2, P90: 25.1, P95: 35.7, P99: 85.3, Max: 120.5),
            HttpReqs: new(Count: 15000, Rate: 125.5),
            HttpReqFailed: new(Count: 3, Rate: 0.0002),
            SummaryPath: "results/k6-summary.json");
}
