using Hone.Core.Config;
using Hone.Core.Models;

namespace Hone.Measurement.Comparison;

/// <summary>
/// Pure comparison engine that determines whether an experiment is accepted,
/// rejected, or stale based on metric deltas and tolerance thresholds.
/// </summary>
public sealed class MetricComparer
{
    /// <summary>
    /// Compares <paramref name="current"/> metrics against <paramref name="previous"/>
    /// and returns an accept/reject decision with per-metric details.
    /// </summary>
    /// <param name="current">Current experiment metrics.</param>
    /// <param name="previous">Reference metrics to compare against.</param>
    /// <param name="baseline">Optional baseline for improvement calculation; defaults to <paramref name="previous"/>.</param>
    /// <param name="tolerances">Tolerance thresholds for accept/reject decisions.</param>
    /// <param name="currentCounters">Optional .NET counter metrics for the current experiment.</param>
    /// <param name="previousCounters">Optional .NET counter metrics for the reference run.</param>
    /// <returns>A fully populated <see cref="ComparisonResult"/>.</returns>
    public static ComparisonResult Compare(
        MetricSet current,
        MetricSet previous,
        MetricSet? baseline,
        TolerancesConfig tolerances,
        RuntimeCounterMetrics? currentCounters = null,
        RuntimeCounterMetrics? previousCounters = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(tolerances);

        baseline ??= previous;

        // ── P95 latency (lower is better) ──────────────────────────────
        double p95Change = GetPctChange(current.HttpReqDuration.P95, previous.HttpReqDuration.P95);
        bool p95Improved = TestImprovement(p95Change, tolerances.MinImprovementPct, lowerIsBetter: true);
        double p95AbsoluteDelta = current.HttpReqDuration.P95 - previous.HttpReqDuration.P95;
        bool p95Regressed = (p95Change > tolerances.MaxRegressionPct)
                            && (p95AbsoluteDelta > tolerances.MinAbsoluteP95DeltaMs);

        // ── RPS (higher is better) ─────────────────────────────────────
        double rpsChange = GetPctChange(current.HttpReqs.Rate, previous.HttpReqs.Rate);
        bool rpsImproved = TestImprovement(rpsChange, tolerances.MinImprovementPct, lowerIsBetter: false);
        double rpsAbsoluteDelta = previous.HttpReqs.Rate - current.HttpReqs.Rate;
        bool rpsRegressed = (rpsChange < -tolerances.MaxRegressionPct)
                            && (rpsAbsoluteDelta > tolerances.MinAbsoluteRPSDelta);

        // ── Error rate (lower is better) ───────────────────────────────
        double errChange = GetPctChange(current.HttpReqFailed.Rate, previous.HttpReqFailed.Rate);
        bool errImproved = TestImprovement(errChange, tolerances.MinImprovementPct, lowerIsBetter: true)
                           && (previous.HttpReqFailed.Rate > 0);
        double errAbsoluteDelta = current.HttpReqFailed.Rate - previous.HttpReqFailed.Rate;
        bool errRegressed = (errChange > tolerances.MaxRegressionPct)
                            && (current.HttpReqFailed.Rate > 0)
                            && (errAbsoluteDelta > tolerances.MinAbsoluteErrorRateDelta);

        // ── Aggregate decisions ────────────────────────────────────────
        bool anyImproved = p95Improved || rpsImproved || errImproved;
        bool anyRegressed = p95Regressed || rpsRegressed || errRegressed;

        // ── Efficiency tiebreaker ──────────────────────────────────────
        bool tiebreakerUsed = false;
        EfficiencyConfig efficiency = tolerances.Efficiency;

        if (efficiency.Enabled && currentCounters is not null && previousCounters is not null)
        {
            double cpuChange = GetPctChange(currentCounters.CpuUsage.Avg, previousCounters.CpuUsage.Avg);
            bool cpuImproved = cpuChange <= -efficiency.MinCpuReductionPct;

            double wsChange = GetPctChange(currentCounters.WorkingSetMB.Max, previousCounters.WorkingSetMB.Max);
            bool workingSetImproved = wsChange <= -efficiency.MinWorkingSetReductionPct;

            bool efficiencyImproved = cpuImproved || workingSetImproved;
            tiebreakerUsed = !anyImproved && !anyRegressed && efficiencyImproved;
        }

        // ── Final outcome ──────────────────────────────────────────────
        bool improved = anyImproved || tiebreakerUsed;
        bool accepted = improved && !anyRegressed;

        ExperimentOutcome outcome = anyRegressed
            ? ExperimentOutcome.Regressed
            : tiebreakerUsed
                ? ExperimentOutcome.EfficiencyWin
                : anyImproved
                    ? ExperimentOutcome.Improved
                    : ExperimentOutcome.Stale;

        // ── Improvement vs baseline ────────────────────────────────────
        double baselineP95 = baseline.HttpReqDuration.P95;
        double improvementPct = baselineP95 > 0
            ? Math.Round((baselineP95 - current.HttpReqDuration.P95) / baselineP95 * 100, 1)
            : 0.0;

        // ── Regression pct (max across regressed metrics) ──────────────
        double regressionPct = 0.0;
        if (anyRegressed)
        {
            double maxReg = 0.0;
            if (p95Regressed)
            {
                maxReg = Math.Max(maxReg, Math.Abs(p95Change));
            }

            if (rpsRegressed)
            {
                maxReg = Math.Max(maxReg, Math.Abs(rpsChange));
            }

            if (errRegressed)
            {
                maxReg = Math.Max(maxReg, Math.Abs(errChange));
            }

            regressionPct = Math.Round(maxReg * 100, 1);
        }

        // ── Build per-metric details ───────────────────────────────────
        List<MetricComparison> details =
        [
            new(
                MetricName: "p95",
                Current: current.HttpReqDuration.P95,
                Previous: previous.HttpReqDuration.P95,
                Baseline: baseline.HttpReqDuration.P95,
                DeltaPct: p95Change,
                AbsoluteDelta: p95AbsoluteDelta,
                Improved: p95Improved,
                Regressed: p95Regressed),
            new(
                MetricName: "rps",
                Current: current.HttpReqs.Rate,
                Previous: previous.HttpReqs.Rate,
                Baseline: baseline.HttpReqs.Rate,
                DeltaPct: rpsChange,
                AbsoluteDelta: rpsAbsoluteDelta,
                Improved: rpsImproved,
                Regressed: rpsRegressed),
            new(
                MetricName: "error_rate",
                Current: current.HttpReqFailed.Rate,
                Previous: previous.HttpReqFailed.Rate,
                Baseline: baseline.HttpReqFailed.Rate,
                DeltaPct: errChange,
                AbsoluteDelta: errAbsoluteDelta,
                Improved: errImproved,
                Regressed: errRegressed),
        ];

        return new ComparisonResult(
            Accepted: accepted,
            Outcome: outcome,
            ImprovementPct: improvementPct,
            RegressionPct: regressionPct,
            Details: details);
    }

    /// <summary>
    /// Computes percentage change between two values, clamped to [-10, 10].
    /// When <paramref name="previous"/> is zero, returns a sentinel value
    /// based on the sign of <paramref name="current"/>.
    /// </summary>
    internal static double GetPctChange(double current, double previous)
    {
        if (previous == 0)
        {
            if (current > 0)
            {
                return 10.0;
            }

            return current < 0 ? -10.0 : 0.0;
        }

        return Math.Clamp((current - previous) / previous, -10.0, 10.0);
    }

    /// <summary>
    /// Determines whether a metric change constitutes an improvement.
    /// </summary>
    internal static bool TestImprovement(double change, double threshold, bool lowerIsBetter)
    {
        if (lowerIsBetter)
        {
            return threshold == 0 ? change < 0 : change <= -threshold;
        }

        return threshold == 0 ? change > 0 : change >= threshold;
    }
}
