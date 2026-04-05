using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Models;

public sealed class QueueItemTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        QueueItem original = new(
            Id: "q-001",
            FilePath: "src/Api/Controllers/OrderController.cs",
            Explanation: "Reduce N+1 queries",
            Scope: OpportunityScope.Narrow,
            Status: QueueItemStatus.Pending,
            TriedByExperiment: null,
            Outcome: null);

        string json = JsonSerializer.Serialize(original);
        QueueItem? deserialized = JsonSerializer.Deserialize<QueueItem>(json);

        _ = deserialized.Should().Be(original);
    }

    [Fact]
    public void QueueItem_StatusTransitions_Valid()
    {
        // Pending → InProgress → Done
        QueueItem pending = new(
            Id: "q-001",
            FilePath: "src/Service.cs",
            Explanation: "Optimize query",
            Scope: OpportunityScope.Narrow,
            Status: QueueItemStatus.Pending,
            TriedByExperiment: null,
            Outcome: null);

        QueueItem inProgress = pending with
        {
            Status = QueueItemStatus.InProgress,
            TriedByExperiment = 1,
        };

        QueueItem done = inProgress with
        {
            Status = QueueItemStatus.Done,
            Outcome = "Improved",
        };

        _ = pending.Status.Should().Be(QueueItemStatus.Pending);
        _ = inProgress.Status.Should().Be(QueueItemStatus.InProgress);
        _ = inProgress.TriedByExperiment.Should().Be(1);
        _ = done.Status.Should().Be(QueueItemStatus.Done);
        _ = done.Outcome.Should().Be("Improved");

        // Pending → Skipped (alternative path)
        QueueItem skipped = pending with
        {
            Status = QueueItemStatus.Skipped,
            Outcome = "Deprioritized",
        };

        _ = skipped.Status.Should().Be(QueueItemStatus.Skipped);
        _ = skipped.Outcome.Should().Be("Deprioritized");

        // Verify all transitions serialize correctly
        foreach (QueueItem item in new[] { pending, inProgress, done, skipped })
        {
            string json = JsonSerializer.Serialize(item);
            QueueItem? deserialized = JsonSerializer.Deserialize<QueueItem>(json);
            _ = deserialized.Should().Be(item);
        }
    }

    [Fact]
    public void RoundTrips_WithAllFieldsPopulated()
    {
        QueueItem original = new(
            Id: "q-042",
            FilePath: "src/Data/Repository.cs",
            Explanation: "Add connection pooling",
            Scope: OpportunityScope.Architecture,
            Status: QueueItemStatus.Done,
            TriedByExperiment: 3,
            Outcome: "EfficiencyWin");

        string json = JsonSerializer.Serialize(original);
        QueueItem? deserialized = JsonSerializer.Deserialize<QueueItem>(json);

        _ = deserialized.Should().Be(original);
    }
}
