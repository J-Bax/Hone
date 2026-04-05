using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Models;

public sealed class AnalyzerReportTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        AnalyzerReport original = new(
            Success: true,
            Report: "Performance improved by 15%",
            Summary: "CPU usage dropped significantly",
            PromptPath: "/prompts/analyze.md",
            ResponsePath: "/responses/result.json");

        string json = JsonSerializer.Serialize(original);
        AnalyzerReport? deserialized = JsonSerializer.Deserialize<AnalyzerReport>(json);

        _ = deserialized.Should().Be(original);
    }

    [Fact]
    public void RoundTrips_WithAllNullFields()
    {
        AnalyzerReport original = new(
            Success: false,
            Report: null,
            Summary: null,
            PromptPath: null,
            ResponsePath: null);

        string json = JsonSerializer.Serialize(original);
        AnalyzerReport? deserialized = JsonSerializer.Deserialize<AnalyzerReport>(json);

        _ = deserialized.Should().Be(original);
    }
}
