using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Xunit;

namespace Hone.Core.Tests.Models;

public sealed class CollectorArtifactsTests
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        CollectorArtifacts original = new(
            Success: true,
            ArtifactPaths: ["trace.nettrace", "counters.csv"]);

        string json = JsonSerializer.Serialize(original);
        CollectorArtifacts? deserialized = JsonSerializer.Deserialize<CollectorArtifacts>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Success.Should().BeTrue();
        _ = deserialized.ArtifactPaths.Should().BeEquivalentTo(original.ArtifactPaths);
    }

    [Fact]
    public void RoundTrips_WithEmptyArtifacts()
    {
        CollectorArtifacts original = new(Success: false, ArtifactPaths: []);

        string json = JsonSerializer.Serialize(original);
        CollectorArtifacts? deserialized = JsonSerializer.Deserialize<CollectorArtifacts>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Success.Should().BeFalse();
        _ = deserialized.ArtifactPaths.Should().BeEmpty();
    }
}
