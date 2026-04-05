using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Xunit;

namespace Hone.Core.Tests.Models;

public sealed class CollectorExportTests
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        CollectorExport original = new(
            Success: true,
            ExportedPaths: ["data.csv", "summary.json"],
            Summary: "Exported 42 data points.");

        string json = JsonSerializer.Serialize(original);
        CollectorExport? deserialized = JsonSerializer.Deserialize<CollectorExport>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Success.Should().BeTrue();
        _ = deserialized.ExportedPaths.Should().BeEquivalentTo(original.ExportedPaths);
        _ = deserialized.Summary.Should().Be(original.Summary);
    }

    [Fact]
    public void RoundTrips_WithNullSummary()
    {
        CollectorExport original = new(
            Success: false,
            ExportedPaths: [],
            Summary: null);

        string json = JsonSerializer.Serialize(original);
        CollectorExport? deserialized = JsonSerializer.Deserialize<CollectorExport>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Success.Should().BeFalse();
        _ = deserialized.ExportedPaths.Should().BeEmpty();
        _ = deserialized.Summary.Should().BeNull();
    }

}
