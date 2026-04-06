using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Xunit;

namespace Hone.Core.Tests.Models;

public sealed class OptimizationQueueTests
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        OptimizationQueue original = new(
            GeneratedByExperiment: 1,
            Items:
            [
                new QueueItem(Id: "q-001", FilePath: "src/A.cs", Explanation: "Fix A", Scope: OpportunityScope.Narrow, Status: QueueItemStatus.Pending, TriedByExperiment: null, Outcome: null),
                new QueueItem(Id: "q-002", FilePath: "src/B.cs", Explanation: "Fix B", Scope: OpportunityScope.Architecture, Status: QueueItemStatus.Done, TriedByExperiment: 2, Outcome: "Improved"),
            ]);

        string json = JsonSerializer.Serialize(original);
        OptimizationQueue? deserialized = JsonSerializer.Deserialize<OptimizationQueue>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.GeneratedByExperiment.Should().Be(1);
        _ = deserialized.Items.Should().HaveCount(2);
        _ = deserialized.Items[0].Should().Be(original.Items[0]);
        _ = deserialized.Items[1].Should().Be(original.Items[1]);
    }

    [Fact]
    public void RoundTrips_WithEmptyItems()
    {
        OptimizationQueue original = new(GeneratedByExperiment: 0, Items: []);

        string json = JsonSerializer.Serialize(original);
        OptimizationQueue? deserialized = JsonSerializer.Deserialize<OptimizationQueue>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.GeneratedByExperiment.Should().Be(0);
        _ = deserialized.Items.Should().BeEmpty();
    }
}
