using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Xunit;

namespace Hone.Core.Tests.Models;

public sealed class RunMetadataTests
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        RunMetadata original = new(
            TargetName: "MyWebApi",
            StartedAt: "2024-01-15T10:00:00Z",
            MachineInfo: new MachineInfo("Intel i9-13900K", 24, 64.0m, "Windows 11", "10.0.0"),
            Experiments:
            [
                new ExperimentMetadata(
                    1, "2024-01-15T10:00:00Z", "2024-01-15T10:30:00Z",
                    ExperimentOutcome.Improved, "hone/exp-1", "main",
                    35.7, 125.5, 42, new Uri("https://github.com/org/repo/pull/42"), 0, 0),
            ]);

        string json = JsonSerializer.Serialize(original);
        RunMetadata? deserialized = JsonSerializer.Deserialize<RunMetadata>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.TargetName.Should().Be("MyWebApi");
        _ = deserialized.StartedAt.Should().Be("2024-01-15T10:00:00Z");
        _ = deserialized.MachineInfo.Should().Be(original.MachineInfo);
        _ = deserialized.Experiments.Should().HaveCount(1);
        _ = deserialized.Experiments[0].Experiment.Should().Be(1);
        _ = deserialized.Experiments[0].Outcome.Should().Be(ExperimentOutcome.Improved);
    }

    [Fact]
    public void RoundTrips_WithNullMachineInfo()
    {
        RunMetadata original = new(
            TargetName: "MyApp",
            StartedAt: "2024-01-15T10:00:00Z",
            MachineInfo: null,
            Experiments: []);

        string json = JsonSerializer.Serialize(original);
        RunMetadata? deserialized = JsonSerializer.Deserialize<RunMetadata>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.TargetName.Should().Be("MyApp");
        _ = deserialized.MachineInfo.Should().BeNull();
        _ = deserialized.Experiments.Should().BeEmpty();
    }

}
