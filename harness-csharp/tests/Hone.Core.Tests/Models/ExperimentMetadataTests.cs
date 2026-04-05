using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Models;

public sealed class ExperimentMetadataTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        ExperimentMetadata original = new(
            Experiment: 1,
            StartedAt: "2024-01-15T10:00:00Z",
            CompletedAt: "2024-01-15T10:30:00Z",
            Outcome: ExperimentOutcome.Improved,
            BranchName: "hone/experiment-1",
            BaseBranch: "main",
            P95: 35.7,
            RPS: 125.5,
            PrNumber: 42,
            PrUrl: new Uri("https://github.com/org/repo/pull/42"),
            StaleCount: 0,
            ConsecutiveFailures: 0);

        string json = JsonSerializer.Serialize(original);
        ExperimentMetadata? deserialized = JsonSerializer.Deserialize<ExperimentMetadata>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Experiment.Should().Be(1);
        _ = deserialized.StartedAt.Should().Be("2024-01-15T10:00:00Z");
        _ = deserialized.CompletedAt.Should().Be("2024-01-15T10:30:00Z");
        _ = deserialized.Outcome.Should().Be(ExperimentOutcome.Improved);
        _ = deserialized.BranchName.Should().Be("hone/experiment-1");
        _ = deserialized.BaseBranch.Should().Be("main");
        _ = deserialized.P95.Should().Be(35.7);
        _ = deserialized.RPS.Should().Be(125.5);
        _ = deserialized.PrNumber.Should().Be(42);
        _ = deserialized.PrUrl.Should().Be(new Uri("https://github.com/org/repo/pull/42"));
        _ = deserialized.StaleCount.Should().Be(0);
        _ = deserialized.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public void ExperimentMetadata_AdditionalProperties_Preserved()
    {
        const string JsonWithExtras = """
            {
                "Experiment": 2,
                "StartedAt": "2024-01-15T11:00:00Z",
                "CompletedAt": null,
                "Outcome": null,
                "BranchName": null,
                "BaseBranch": null,
                "P95": null,
                "RPS": null,
                "PrNumber": null,
                "PrUrl": null,
                "StaleCount": 0,
                "ConsecutiveFailures": 0,
                "CustomField": "custom-value",
                "NumericExtra": 42
            }
            """;

        ExperimentMetadata? deserialized = JsonSerializer.Deserialize<ExperimentMetadata>(JsonWithExtras);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Experiment.Should().Be(2);
        _ = deserialized.AdditionalProperties.Should().NotBeNull();
        _ = deserialized.AdditionalProperties.Should().ContainKey("CustomField");
        _ = deserialized.AdditionalProperties!["CustomField"].GetString().Should().Be("custom-value");
        _ = deserialized.AdditionalProperties.Should().ContainKey("NumericExtra");
        _ = deserialized.AdditionalProperties["NumericExtra"].GetInt32().Should().Be(42);

        // Round-trip: extra properties should survive serialization
        string reserializedJson = JsonSerializer.Serialize(deserialized);
        ExperimentMetadata? roundTripped = JsonSerializer.Deserialize<ExperimentMetadata>(reserializedJson);

        _ = roundTripped.Should().NotBeNull();
        _ = roundTripped!.AdditionalProperties.Should().ContainKey("CustomField");
        _ = roundTripped.AdditionalProperties!["CustomField"].GetString().Should().Be("custom-value");
        _ = roundTripped.AdditionalProperties.Should().ContainKey("NumericExtra");
        _ = roundTripped.AdditionalProperties["NumericExtra"].GetInt32().Should().Be(42);
    }

    [Fact]
    public void RoundTrips_WithAllNullOptionalFields()
    {
        ExperimentMetadata original = new(
            Experiment: 1,
            StartedAt: "2024-01-15T10:00:00Z",
            CompletedAt: null,
            Outcome: null,
            BranchName: null,
            BaseBranch: null,
            P95: null,
            RPS: null,
            PrNumber: null,
            PrUrl: null,
            StaleCount: 3,
            ConsecutiveFailures: 1);

        string json = JsonSerializer.Serialize(original);
        ExperimentMetadata? deserialized = JsonSerializer.Deserialize<ExperimentMetadata>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Experiment.Should().Be(1);
        _ = deserialized.CompletedAt.Should().BeNull();
        _ = deserialized.Outcome.Should().BeNull();
        _ = deserialized.BranchName.Should().BeNull();
        _ = deserialized.BaseBranch.Should().BeNull();
        _ = deserialized.P95.Should().BeNull();
        _ = deserialized.RPS.Should().BeNull();
        _ = deserialized.PrNumber.Should().BeNull();
        _ = deserialized.PrUrl.Should().BeNull();
        _ = deserialized.StaleCount.Should().Be(3);
        _ = deserialized.ConsecutiveFailures.Should().Be(1);
    }

    [Fact]
    public void Outcome_SerializesAsString()
    {
        ExperimentMetadata metadata = new(
            Experiment: 1,
            StartedAt: "2024-01-15T10:00:00Z",
            CompletedAt: null,
            Outcome: ExperimentOutcome.Regressed,
            BranchName: null,
            BaseBranch: null,
            P95: null,
            RPS: null,
            PrNumber: null,
            PrUrl: null,
            StaleCount: 0,
            ConsecutiveFailures: 0);

        string json = JsonSerializer.Serialize(metadata);
        _ = json.Should().Contain("\"Regressed\"");
    }
}
