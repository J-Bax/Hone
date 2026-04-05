using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Hone.Core.Models;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Models;

public sealed class EnumTests(ITestOutputHelper output) : HoneTestBase(output)
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

    [Fact]
    public void ExperimentOutcome_HasExpectedMemberCount()
    {
        _ = Enum.GetValues<ExperimentOutcome>().Should().HaveCount(4);
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

    [Fact]
    public void OpportunityScope_HasExpectedMemberCount()
    {
        _ = Enum.GetValues<OpportunityScope>().Should().HaveCount(2);
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

    [Fact]
    public void QueueItemStatus_HasExpectedMemberCount()
    {
        _ = Enum.GetValues<QueueItemStatus>().Should().HaveCount(4);
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

    [Fact]
    public void LogLevel_HasExpectedMemberCount()
    {
        _ = Enum.GetValues<LogLevel>().Should().HaveCount(4);
    }
}
