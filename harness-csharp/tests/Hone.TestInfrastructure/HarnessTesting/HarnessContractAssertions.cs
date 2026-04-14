using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Hone.Core.Models;

namespace Hone.TestInfrastructure.HarnessTesting;

public sealed record ExpectedExperimentContract(
    int Experiment,
    ExperimentOutcome? Outcome,
    string BranchName,
    string BaseBranch,
    bool HasPullRequest,
    bool HasMetrics);

public sealed record ExpectedQueueItemContract(
    string Id,
    string FilePath,
    QueueItemStatus Status,
    int? TriedByExperiment,
    string? Outcome);

/// <summary>
/// Reusable behavioral assertions for the harness-testing scenario suite.
/// </summary>
public static class HarnessContractAssertions
{
    private static readonly JsonSerializerOptions QueueJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static void AssertRunMetadataContracts(
        RunMetadata metadata,
        string expectedTargetName,
        IReadOnlyList<ExpectedExperimentContract> expectedExperiments)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(expectedExperiments);

        _ = metadata.TargetName.Should().Be(expectedTargetName);
        _ = metadata.StartedAt.Should().NotBeNullOrWhiteSpace();
        _ = metadata.Experiments.Should().HaveCount(expectedExperiments.Count);
        _ = metadata.Experiments.Select(e => e.Experiment)
            .Should().Equal(expectedExperiments.Select(e => e.Experiment));

        foreach (ExpectedExperimentContract expected in expectedExperiments)
        {
            ExperimentMetadata actual = metadata.Experiments.Single(e => e.Experiment == expected.Experiment);

            _ = actual.StartedAt.Should().NotBeNullOrWhiteSpace();
            _ = actual.CompletedAt.Should().NotBeNullOrWhiteSpace();
            _ = actual.Outcome.Should().Be(expected.Outcome);
            _ = actual.BranchName.Should().Be(expected.BranchName);
            _ = actual.BaseBranch.Should().Be(expected.BaseBranch);
            _ = actual.StaleCount.Should().BeGreaterThanOrEqualTo(0);
            _ = actual.ConsecutiveFailures.Should().BeGreaterThanOrEqualTo(0);

            if (expected.HasMetrics)
            {
                _ = actual.P95.Should().NotBeNull();
                _ = actual.RPS.Should().NotBeNull();
            }
            else
            {
                _ = actual.P95.Should().BeNull();
                _ = actual.RPS.Should().BeNull();
            }

            if (expected.HasPullRequest)
            {
                _ = actual.PrNumber.Should().NotBeNull();
                _ = actual.PrUrl.Should().NotBeNull();
            }
            else
            {
                _ = actual.PrNumber.Should().BeNull();
                _ = actual.PrUrl.Should().BeNull();
            }
        }
    }

    public static void AssertSuccessfulBranchLineage(
        RunMetadata metadata,
        string defaultBranch = "main")
    {
        ArgumentNullException.ThrowIfNull(metadata);

        string expectedBase = defaultBranch;
        foreach (ExperimentMetadata experiment in metadata.Experiments
                     .Where(e => e.Outcome is ExperimentOutcome.Improved or ExperimentOutcome.EfficiencyWin))
        {
            _ = experiment.BaseBranch.Should().Be(expectedBase);
            _ = experiment.BranchName.Should().NotBeNullOrWhiteSpace();
            expectedBase = experiment.BranchName!;
        }
    }

    public static void AssertQueueContracts(
        string queueJsonPath,
        int expectedGeneratedByExperiment,
        IReadOnlyList<ExpectedQueueItemContract> expectedItems)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueJsonPath);
        ArgumentNullException.ThrowIfNull(expectedItems);

        _ = File.Exists(queueJsonPath).Should().BeTrue($"queue snapshot should exist at {queueJsonPath}");

        using FileStream stream = File.OpenRead(queueJsonPath);
        QueueSnapshot snapshot = JsonSerializer.Deserialize<QueueSnapshot>(stream, QueueJsonOptions)
            ?? throw new InvalidOperationException($"Queue snapshot '{queueJsonPath}' could not be deserialized.");

        _ = snapshot.GeneratedByExperiment.Should().Be(expectedGeneratedByExperiment);
        _ = snapshot.Items.Should().HaveCount(expectedItems.Count);

        foreach (ExpectedQueueItemContract expected in expectedItems)
        {
            QueueItemSnapshot actual = snapshot.Items.Single(item => string.Equals(item.Id, expected.Id, StringComparison.Ordinal));
            _ = actual.FilePath.Should().Be(expected.FilePath);
            _ = actual.Status.Should().Be(expected.Status);
            _ = actual.TriedByExperiment.Should().Be(expected.TriedByExperiment);
            _ = actual.Outcome.Should().Be(expected.Outcome);
        }
    }

    private sealed record QueueSnapshot(
        int GeneratedByExperiment,
        IReadOnlyList<QueueItemSnapshot> Items);

    private sealed record QueueItemSnapshot(
        string Id,
        string FilePath,
        QueueItemStatus Status,
        int? TriedByExperiment,
        string? Outcome);
}
