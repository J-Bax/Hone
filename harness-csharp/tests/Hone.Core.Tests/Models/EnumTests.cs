using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Hone.Core.Models;
using Xunit;

namespace Hone.Core.Tests.Models;

public sealed class EnumTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Theory]
    [InlineData(ExperimentOutcome.Improved, "\"Improved\"")]
    [InlineData(ExperimentOutcome.Regressed, "\"Regressed\"")]
    [InlineData(ExperimentOutcome.Stale, "\"Stale\"")]
    [InlineData(ExperimentOutcome.EfficiencyWin, "\"EfficiencyWin\"")]
    public void ExperimentOutcome_RoundTrips(ExperimentOutcome value, string expectedJson)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions);
        _ = json.Should().Be(expectedJson);

        ExperimentOutcome deserialized = JsonSerializer.Deserialize<ExperimentOutcome>(json, JsonOptions);
        _ = deserialized.Should().Be(value);
    }

    [Theory]
    [InlineData(OpportunityScope.Narrow, "\"Narrow\"")]
    [InlineData(OpportunityScope.Architecture, "\"Architecture\"")]
    public void OpportunityScope_RoundTrips(OpportunityScope value, string expectedJson)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions);
        _ = json.Should().Be(expectedJson);

        OpportunityScope deserialized = JsonSerializer.Deserialize<OpportunityScope>(json, JsonOptions);
        _ = deserialized.Should().Be(value);
    }

    [Theory]
    [InlineData(QueueItemStatus.Pending, "\"Pending\"")]
    [InlineData(QueueItemStatus.InProgress, "\"InProgress\"")]
    [InlineData(QueueItemStatus.Done, "\"Done\"")]
    [InlineData(QueueItemStatus.Skipped, "\"Skipped\"")]
    public void QueueItemStatus_RoundTrips(QueueItemStatus value, string expectedJson)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions);
        _ = json.Should().Be(expectedJson);

        QueueItemStatus deserialized = JsonSerializer.Deserialize<QueueItemStatus>(json, JsonOptions);
        _ = deserialized.Should().Be(value);
    }

    [Theory]
    [InlineData(LogLevel.Verbose, "\"Verbose\"")]
    [InlineData(LogLevel.Info, "\"Info\"")]
    [InlineData(LogLevel.Warning, "\"Warning\"")]
    [InlineData(LogLevel.Error, "\"Error\"")]
    public void LogLevel_RoundTrips(LogLevel value, string expectedJson)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions);
        _ = json.Should().Be(expectedJson);

        LogLevel deserialized = JsonSerializer.Deserialize<LogLevel>(json, JsonOptions);
        _ = deserialized.Should().Be(value);
    }
}
