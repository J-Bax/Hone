using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Xunit;

namespace Hone.Core.Tests.Models;

public sealed class ComparisonResultTests
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        ComparisonResult original = new(
            Accepted: true,
            Outcome: ExperimentOutcome.Improved,
            ImprovementPct: 15.2,
            RegressionPct: 0.0,
            Details:
            [
                new MetricComparison(
                    MetricName: "p95", Current: 35.7, Previous: 42.1, Baseline: 40.0,
                    DeltaPct: -15.2, AbsoluteDelta: -6.4, Improved: true, Regressed: false),
                new MetricComparison(
                    MetricName: "rps", Current: 125.5, Previous: 120.0, Baseline: null,
                    DeltaPct: 4.6, AbsoluteDelta: 5.5, Improved: true, Regressed: false),
            ]);

        string json = JsonSerializer.Serialize(original);
        ComparisonResult? deserialized = JsonSerializer.Deserialize<ComparisonResult>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Accepted.Should().BeTrue();
        _ = deserialized.Outcome.Should().Be(ExperimentOutcome.Improved);
        _ = deserialized.ImprovementPct.Should().Be(15.2);
        _ = deserialized.RegressionPct.Should().Be(0.0);
        _ = deserialized.Details.Should().HaveCount(2);
        _ = deserialized.Details[0].Should().Be(original.Details[0]);
        _ = deserialized.Details[1].Should().Be(original.Details[1]);
    }

    [Fact]
    public void ComparisonResult_OutcomeEnum_CoversAllCases()
    {
        ExperimentOutcome[] allOutcomes =
        [
            ExperimentOutcome.Improved,
            ExperimentOutcome.Regressed,
            ExperimentOutcome.Stale,
            ExperimentOutcome.EfficiencyWin,
            ExperimentOutcome.ImplementationFailed,
            ExperimentOutcome.BuildFailed,
            ExperimentOutcome.TestFailed,
            ExperimentOutcome.StartFailed,
            ExperimentOutcome.LoadTestFailed,
        ];

        foreach (ExperimentOutcome outcome in allOutcomes)
        {
            ComparisonResult result = new(
                Accepted: outcome is ExperimentOutcome.Improved or ExperimentOutcome.EfficiencyWin,
                Outcome: outcome,
                ImprovementPct: 0.0,
                RegressionPct: 0.0,
                Details: []);

            string json = JsonSerializer.Serialize(result);
            ComparisonResult? deserialized = JsonSerializer.Deserialize<ComparisonResult>(json);

            _ = deserialized.Should().NotBeNull();
            _ = deserialized!.Outcome.Should().Be(outcome);

            // Verify enum serializes as string
            _ = json.Should().Contain($"\"{outcome}\"");
        }
    }

}
