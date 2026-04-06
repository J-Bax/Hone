using FluentAssertions;
using Hone.Core.Config;
using Hone.Core.Models;
using Hone.Measurement.Comparison;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Measurement.Tests.Comparison;

public sealed class MetricComparerTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private static readonly TolerancesConfig DefaultTolerances = new();

    [Fact]
    public void Compare_FlatMetrics_ReturnsStale()
    {
        MetricSet current = MakeMetrics(p95: 100, rps: 500, errorRate: 0.01);
        MetricSet previous = MakeMetrics(p95: 100, rps: 500, errorRate: 0.01);

        ComparisonResult result = MetricComparer.Compare(current, previous, baseline: null, DefaultTolerances);

        _ = result.Outcome.Should().Be(ExperimentOutcome.Stale);
        _ = result.Accepted.Should().BeFalse();
        _ = result.Details.Should().HaveCount(3);
        _ = result.Details.Should().OnlyContain(d => !d.Improved && !d.Regressed);
    }

    [Fact]
    public void Compare_ImprovedP95_NoRegression_Accepted()
    {
        MetricSet current = MakeMetrics(p95: 80, rps: 500, errorRate: 0.01);
        MetricSet previous = MakeMetrics(p95: 100, rps: 500, errorRate: 0.01);

        ComparisonResult result = MetricComparer.Compare(current, previous, baseline: null, DefaultTolerances);

        _ = result.Outcome.Should().Be(ExperimentOutcome.Improved);
        _ = result.Accepted.Should().BeTrue();
        _ = result.ImprovementPct.Should().Be(20.0);

        MetricComparison p95Detail = GetDetail(result, "p95");
        _ = p95Detail.Improved.Should().BeTrue();
        _ = p95Detail.Regressed.Should().BeFalse();
        _ = p95Detail.DeltaPct.Should().BeApproximately(-0.20, 1e-9);
    }

    [Fact]
    public void Compare_ImprovedRPS_NoRegression_Accepted()
    {
        MetricSet current = MakeMetrics(p95: 100, rps: 575, errorRate: 0.01);
        MetricSet previous = MakeMetrics(p95: 100, rps: 500, errorRate: 0.01);

        ComparisonResult result = MetricComparer.Compare(current, previous, baseline: null, DefaultTolerances);

        _ = result.Outcome.Should().Be(ExperimentOutcome.Improved);
        _ = result.Accepted.Should().BeTrue();

        MetricComparison rpsDetail = GetDetail(result, "rps");
        _ = rpsDetail.Improved.Should().BeTrue();
        _ = rpsDetail.Regressed.Should().BeFalse();
        _ = rpsDetail.DeltaPct.Should().BeApproximately(0.15, 1e-9);
    }

    [Fact]
    public void Compare_RegressionP95_BeyondThreshold_Rejected()
    {
        MetricSet current = MakeMetrics(p95: 115, rps: 500, errorRate: 0.01);
        MetricSet previous = MakeMetrics(p95: 100, rps: 500, errorRate: 0.01);

        ComparisonResult result = MetricComparer.Compare(current, previous, baseline: null, DefaultTolerances);

        _ = result.Outcome.Should().Be(ExperimentOutcome.Regressed);
        _ = result.Accepted.Should().BeFalse();
        _ = result.RegressionPct.Should().Be(15.0);

        MetricComparison p95Detail = GetDetail(result, "p95");
        _ = p95Detail.Regressed.Should().BeTrue();
        _ = p95Detail.AbsoluteDelta.Should().Be(15.0);
    }

    [Fact]
    public void Compare_RegressionP95_BelowAbsoluteThreshold_NotRegression()
    {
        MetricSet current = MakeMetrics(p95: 23, rps: 500, errorRate: 0.01);
        MetricSet previous = MakeMetrics(p95: 20, rps: 500, errorRate: 0.01);

        ComparisonResult result = MetricComparer.Compare(current, previous, baseline: null, DefaultTolerances);

        _ = result.Outcome.Should().Be(ExperimentOutcome.Stale);
        _ = result.Accepted.Should().BeFalse();

        MetricComparison p95Detail = GetDetail(result, "p95");
        _ = p95Detail.Regressed.Should().BeFalse();
    }

    [Fact]
    public void Compare_RegressionRPS_BelowAbsoluteThreshold_NotRegression()
    {
        MetricSet current = MakeMetrics(p95: 100, rps: 22, errorRate: 0.01);
        MetricSet previous = MakeMetrics(p95: 100, rps: 25, errorRate: 0.01);

        ComparisonResult result = MetricComparer.Compare(current, previous, baseline: null, DefaultTolerances);

        _ = result.Outcome.Should().Be(ExperimentOutcome.Stale);
        _ = result.Accepted.Should().BeFalse();

        MetricComparison rpsDetail = GetDetail(result, "rps");
        _ = rpsDetail.Regressed.Should().BeFalse();
    }

    [Fact]
    public void Compare_MixedSignals_ImprovementAndRegression_Rejected()
    {
        MetricSet current = MakeMetrics(p95: 80, rps: 400, errorRate: 0.01);
        MetricSet previous = MakeMetrics(p95: 100, rps: 500, errorRate: 0.01);

        ComparisonResult result = MetricComparer.Compare(current, previous, baseline: null, DefaultTolerances);

        _ = result.Outcome.Should().Be(ExperimentOutcome.Regressed);
        _ = result.Accepted.Should().BeFalse();

        MetricComparison p95Detail = GetDetail(result, "p95");
        _ = p95Detail.Improved.Should().BeTrue();

        MetricComparison rpsDetail = GetDetail(result, "rps");
        _ = rpsDetail.Regressed.Should().BeTrue();
    }

    [Fact]
    public void Compare_EfficiencyTiebreaker_FlatPerf_CpuDown_Accepted()
    {
        MetricSet current = MakeMetrics(p95: 100, rps: 500, errorRate: 0.01);
        MetricSet previous = MakeMetrics(p95: 100, rps: 500, errorRate: 0.01);

        RuntimeCounterMetrics currentCounters = MakeCounters(cpuAvg: 0.45, workingSetMax: 200);
        RuntimeCounterMetrics previousCounters = MakeCounters(cpuAvg: 0.50, workingSetMax: 200);

        ComparisonResult result = MetricComparer.Compare(
            current, previous, baseline: null, DefaultTolerances,
            currentCounters, previousCounters);

        _ = result.Outcome.Should().Be(ExperimentOutcome.EfficiencyWin);
        _ = result.Accepted.Should().BeTrue();
    }

    [Fact]
    public void Compare_EfficiencyTiebreaker_Disabled_StaysStale()
    {
        MetricSet current = MakeMetrics(p95: 100, rps: 500, errorRate: 0.01);
        MetricSet previous = MakeMetrics(p95: 100, rps: 500, errorRate: 0.01);

        RuntimeCounterMetrics currentCounters = MakeCounters(cpuAvg: 0.45, workingSetMax: 200);
        RuntimeCounterMetrics previousCounters = MakeCounters(cpuAvg: 0.50, workingSetMax: 200);

        var tolerances = new TolerancesConfig(Efficiency: new EfficiencyConfig(Enabled: false));

        ComparisonResult result = MetricComparer.Compare(
            current, previous, baseline: null, tolerances,
            currentCounters, previousCounters);

        _ = result.Outcome.Should().Be(ExperimentOutcome.Stale);
        _ = result.Accepted.Should().BeFalse();
    }

    [Fact]
    public void Compare_EfficiencyTiebreaker_DoesNotOverrideRegression()
    {
        MetricSet current = MakeMetrics(p95: 115, rps: 500, errorRate: 0.01);
        MetricSet previous = MakeMetrics(p95: 100, rps: 500, errorRate: 0.01);

        RuntimeCounterMetrics currentCounters = MakeCounters(cpuAvg: 0.45, workingSetMax: 200);
        RuntimeCounterMetrics previousCounters = MakeCounters(cpuAvg: 0.50, workingSetMax: 200);

        ComparisonResult result = MetricComparer.Compare(
            current, previous, baseline: null, DefaultTolerances,
            currentCounters, previousCounters);

        _ = result.Outcome.Should().Be(ExperimentOutcome.Regressed);
        _ = result.Accepted.Should().BeFalse();
    }

    [Fact]
    public void Compare_ZeroBaseline_ErrorRateRise_MaxDelta()
    {
        MetricSet current = MakeMetrics(p95: 100, rps: 500, errorRate: 0.05);
        MetricSet previous = MakeMetrics(p95: 100, rps: 500, errorRate: 0);

        ComparisonResult result = MetricComparer.Compare(current, previous, baseline: null, DefaultTolerances);

        _ = result.Outcome.Should().Be(ExperimentOutcome.Regressed);
        _ = result.Accepted.Should().BeFalse();

        MetricComparison errDetail = GetDetail(result, "error_rate");
        _ = errDetail.Regressed.Should().BeTrue();
        _ = errDetail.DeltaPct.Should().Be(10.0);
    }

    [Fact]
    public void Compare_ErrorRateRegression_BelowAbsoluteThreshold_Ignored()
    {
        MetricSet current = MakeMetrics(p95: 100, rps: 500, errorRate: 0.006);
        MetricSet previous = MakeMetrics(p95: 100, rps: 500, errorRate: 0.004);

        ComparisonResult result = MetricComparer.Compare(current, previous, baseline: null, DefaultTolerances);

        _ = result.Outcome.Should().Be(ExperimentOutcome.Stale);
        _ = result.Accepted.Should().BeFalse();

        MetricComparison errDetail = GetDetail(result, "error_rate");
        _ = errDetail.Regressed.Should().BeFalse();
    }

    #region Helpers

    private static MetricComparison GetDetail(ComparisonResult result, string metricName) =>
        result.Details.Single(d => string.Equals(d.MetricName, metricName, StringComparison.Ordinal));

    private static MetricSet MakeMetrics(double p95, double rps, double errorRate)
    {
        return new MetricSet(
            Timestamp: "2024-01-01T00:00:00Z",
            Experiment: 1,
            Run: 1,
            HttpReqDuration: new HttpReqDurationMetrics(
                Avg: p95 * 0.8,
                P50: p95 * 0.7,
                P90: p95 * 0.95,
                P95: p95,
                P99: p95 * 1.1,
                Max: p95 * 1.5),
            HttpReqs: new HttpReqCountMetrics(
                Count: 1000,
                Rate: rps),
            HttpReqFailed: new HttpReqFailedMetrics(
                Count: (long)(1000 * errorRate),
                Rate: errorRate),
            SummaryPath: null);
    }

    private static RuntimeCounterMetrics MakeCounters(
        double cpuAvg = 0.50,
        double workingSetMax = 200)
    {
        var zero = new CounterStatistic(Avg: 0, Min: 0, Max: 0, Last: 0, Samples: 1);

        return new RuntimeCounterMetrics(
            CpuUsage: new CounterStatistic(
                Avg: cpuAvg, Min: cpuAvg * 0.8, Max: cpuAvg * 1.2,
                Last: cpuAvg, Samples: 10),
            WorkingSetMB: new CounterStatistic(
                Avg: workingSetMax * 0.9, Min: workingSetMax * 0.8, Max: workingSetMax,
                Last: workingSetMax * 0.95, Samples: 10),
            GcHeapSizeMB: zero,
            Gen0Collections: zero,
            Gen1Collections: zero,
            Gen2Collections: zero,
            GcPauseRatio: zero,
            ThreadPoolThreads: zero,
            ThreadPoolQueueLength: zero,
            ExceptionCount: zero,
            AllocRateMB: zero);
    }

    #endregion
}
