using System.Globalization;
using FluentAssertions;
using Hone.Core.Models;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Measurement.K6.Tests;

public sealed class K6SummaryParserTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public async Task K6SummaryParser_RealFixture_AllFieldsMapped()
    {
        // Arrange
        string fixturePath = FindFixturePath("k6-summary-sample.json");

        // Act
        MetricSet result = await K6SummaryParser.ParseAsync(fixturePath, experiment: 1, run: 1);

        // Assert
        _ = result.Experiment.Should().Be(1);
        _ = result.Run.Should().Be(1);
        _ = result.SummaryPath.Should().Be(fixturePath);
        _ = DateTimeOffset.Parse(result.Timestamp, CultureInfo.InvariantCulture)
            .Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));

        _ = result.HttpReqDuration.Avg.Should().Be(150.0);
        _ = result.HttpReqDuration.P50.Should().Be(100.0);
        _ = result.HttpReqDuration.P90.Should().Be(200.0);
        _ = result.HttpReqDuration.P95.Should().Be(300.0);
        _ = result.HttpReqDuration.P99.Should().Be(450.0);
        _ = result.HttpReqDuration.Max.Should().Be(500.0);

        _ = result.HttpReqs.Count.Should().Be(1000);
        _ = result.HttpReqs.Rate.Should().Be(50.5);

        _ = result.HttpReqFailed.Count.Should().Be(10);
        _ = result.HttpReqFailed.Rate.Should().Be(0.01);
    }

    [Fact]
    public void ParseContent_ValidJson_ReturnsCorrectMetricSet()
    {
        // Arrange
        const string Json = """
            {
              "metrics": {
                "http_req_duration": {
                  "avg": 1911.5,
                  "med": 305.74,
                  "max": 19823.24,
                  "p(90)": 5853.56,
                  "p(95)": 7546.10,
                  "p(99)": 15000.0
                },
                "http_reqs": {
                  "count": 18540,
                  "rate": 125.53
                },
                "http_req_failed": {
                  "passes": 5,
                  "fails": 18535,
                  "value": 0.00027
                }
              }
            }
            """;

        // Act
        MetricSet result = K6SummaryParser.ParseContent(Json, experiment: 3, run: 2, summaryPath: "/path/to/summary.json");

        // Assert
        _ = result.Experiment.Should().Be(3);
        _ = result.Run.Should().Be(2);
        _ = result.SummaryPath.Should().Be("/path/to/summary.json");

        _ = result.HttpReqDuration.Avg.Should().Be(1911.5);
        _ = result.HttpReqDuration.P50.Should().Be(305.74);
        _ = result.HttpReqDuration.P90.Should().Be(5853.56);
        _ = result.HttpReqDuration.P95.Should().Be(7546.10);
        _ = result.HttpReqDuration.P99.Should().Be(15000.0);
        _ = result.HttpReqDuration.Max.Should().Be(19823.24);

        _ = result.HttpReqs.Count.Should().Be(18540);
        _ = result.HttpReqs.Rate.Should().Be(125.53);

        _ = result.HttpReqFailed.Count.Should().Be(5);
        _ = result.HttpReqFailed.Rate.Should().Be(0.00027);
    }

    [Fact]
    public void ParseContent_NullFailedPasses_DefaultsToZero()
    {
        // Arrange — "passes" field is absent from http_req_failed
        const string Json = """
            {
              "metrics": {
                "http_req_duration": {
                  "avg": 100.0, "med": 50.0, "max": 200.0,
                  "p(90)": 150.0, "p(95)": 175.0, "p(99)": 190.0
                },
                "http_reqs": { "count": 100, "rate": 10.0 },
                "http_req_failed": {
                  "fails": 100,
                  "value": 0.0
                }
              }
            }
            """;

        // Act
        MetricSet result = K6SummaryParser.ParseContent(Json, experiment: 1, run: 1);

        // Assert
        _ = result.HttpReqFailed.Count.Should().Be(0);
    }

    [Fact]
    public void ParseContent_NullFailedValue_DefaultsToZero()
    {
        // Arrange — "value" field is absent from http_req_failed
        const string Json = """
            {
              "metrics": {
                "http_req_duration": {
                  "avg": 100.0, "med": 50.0, "max": 200.0,
                  "p(90)": 150.0, "p(95)": 175.0, "p(99)": 190.0
                },
                "http_reqs": { "count": 100, "rate": 10.0 },
                "http_req_failed": {
                  "passes": 3,
                  "fails": 97
                }
              }
            }
            """;

        // Act
        MetricSet result = K6SummaryParser.ParseContent(Json, experiment: 1, run: 1);

        // Assert
        _ = result.HttpReqFailed.Rate.Should().Be(0.0);
    }

    private static string FindFixturePath(string relativePath)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            string candidate = Path.Combine(directory, "test-fixtures", relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new FileNotFoundException($"Test fixture not found: {relativePath}");
    }
}
