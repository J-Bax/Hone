using System.Globalization;
using System.Text;
using Hone.Core.Models;

namespace Hone.Reporting.Rca;

/// <summary>
/// Builds a root cause analysis markdown document from structured inputs.
/// Pure template — no I/O, no file system access.
/// Replaces <c>Export-ExperimentRCA.ps1</c>.
/// </summary>
internal static class RcaBuilder
{
    private const string P95LatencyMetric = "P95Latency";
    private const string RpsMetric = "RPS";
    private const string ErrorRateMetric = "ErrorRate";

    /// <summary>
    /// Generates the complete root cause analysis markdown string.
    /// </summary>
    public static string Build(RcaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sb = new StringBuilder();

        AppendHeader(sb, options);
        AppendPerformanceIssue(sb, options);
        AppendImpactEstimate(sb, options);
        AppendEfficiencySummary(sb, options);
        AppendEfficiencyMetrics(sb, options);
        AppendRootCause(sb, options);
        AppendIterationSummary(sb, options);
        AppendProposedFix(sb, options);

        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, RcaOptions options)
    {
        DateTimeOffset ts = options.GeneratedAtUtc ?? DateTimeOffset.UtcNow;
        string formatted = ts.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

        sb.AppendLine(CultureInfo.InvariantCulture, $"# Root Cause Analysis \u2014 Experiment {options.Experiment}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"> Generated: {formatted}");
        sb.AppendLine();
    }

    private static void AppendPerformanceIssue(StringBuilder sb, RcaOptions options)
    {
        sb.AppendLine("## Performance Issue");
        sb.AppendLine();

        string p95Delta = FormatDeltaCell(options.ComparisonResult, P95LatencyMetric);
        string rpsDelta = FormatDeltaCell(options.ComparisonResult, RpsMetric);
        string errorDelta = FormatDeltaCell(options.ComparisonResult, ErrorRateMetric);

        string currentP95 = options.CurrentMetrics.HttpReqDuration.P95
            .ToString(CultureInfo.InvariantCulture);
        string baselineP95 = options.BaselineMetrics.HttpReqDuration.P95
            .ToString(CultureInfo.InvariantCulture);

        string currentRps = options.CurrentMetrics.HttpReqs.Rate
            .ToString("F1", CultureInfo.InvariantCulture);
        string baselineRps = options.BaselineMetrics.HttpReqs.Rate
            .ToString("F1", CultureInfo.InvariantCulture);

        string currentError = (options.CurrentMetrics.HttpReqFailed.Rate * 100)
            .ToString("F2", CultureInfo.InvariantCulture);
        string baselineError = (options.BaselineMetrics.HttpReqFailed.Rate * 100)
            .ToString("F2", CultureInfo.InvariantCulture);

        sb.AppendLine("| Metric | Current | Baseline | Delta |");
        sb.AppendLine("|--------|---------|----------|-------|");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| p95 Latency | {currentP95}ms | {baselineP95}ms | {p95Delta} |");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| Requests/sec | {currentRps} | {baselineRps} | {rpsDelta} |");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| Error Rate | {currentError}% | {baselineError}% | {errorDelta} |");
        sb.AppendLine();

        if (options.ComparisonResult is not null)
        {
            string improvement = options.ComparisonResult.ImprovementPct
                .ToString("F1", CultureInfo.InvariantCulture);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Overall improvement vs baseline: **{improvement}%** (p95 latency).");
        }

        sb.AppendLine();
    }

    private static void AppendImpactEstimate(StringBuilder sb, RcaOptions options)
    {
        if (options.ImpactEstimate is null)
        {
            return;
        }

        ImpactEstimate est = options.ImpactEstimate;

        sb.AppendLine("## Impact Estimate");
        sb.AppendLine();
        sb.AppendLine("| Metric | Estimate |");
        sb.AppendLine("|--------|----------|");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| Traffic share | {est.TrafficPct.ToString("F1", CultureInfo.InvariantCulture)}% |");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| Latency reduction | {est.LatencyReductionMs.ToString("F1", CultureInfo.InvariantCulture)}ms |");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| Overall p95 improvement | {est.OverallP95ImprovementPct.ToString("F1", CultureInfo.InvariantCulture)}% |");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| Confidence | {est.Confidence} |");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(est.Reasoning))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"> {est.Reasoning}");
            sb.AppendLine();
        }
    }

    private static void AppendEfficiencySummary(StringBuilder sb, RcaOptions options)
    {
        if (options.CounterMetrics is null || options.BaselineCounterMetrics is null)
        {
            return;
        }

        CounterSnapshot current = options.CounterMetrics;
        CounterSnapshot baseline = options.BaselineCounterMetrics;

        string cpuPct = current.CpuAvg.ToString("F1", CultureInfo.InvariantCulture);
        string cpuDelta = FormatPctDelta(current.CpuAvg, baseline.CpuAvg);
        string memMB = current.WorkingSetMBAvg.ToString("F1", CultureInfo.InvariantCulture);
        string memDelta = FormatPctDelta(current.WorkingSetMBAvg, baseline.WorkingSetMBAvg);

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"**Efficiency:** CPU {cpuPct}% ({cpuDelta} delta) | Working Set {memMB}MB ({memDelta} delta)");
        sb.AppendLine();
    }

    private static void AppendEfficiencyMetrics(StringBuilder sb, RcaOptions options)
    {
        if (options.CounterMetrics is null)
        {
            return;
        }

        CounterSnapshot current = options.CounterMetrics;

        string baselineCpuAvg = options.BaselineCounterMetrics is not null
            ? options.BaselineCounterMetrics.CpuAvg.ToString("F1", CultureInfo.InvariantCulture) + "%"
            : "\u2014";

        string baselineMemPeak = options.BaselineCounterMetrics is not null
            ? options.BaselineCounterMetrics.WorkingSetMBMax.ToString("F1", CultureInfo.InvariantCulture) + "MB"
            : "\u2014";

        sb.AppendLine("## Efficiency Metrics");
        sb.AppendLine();
        sb.AppendLine("| Metric | Current | Baseline |");
        sb.AppendLine("|--------|---------|----------|");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| CPU avg | {current.CpuAvg.ToString("F1", CultureInfo.InvariantCulture)}% | {baselineCpuAvg} |");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| CPU peak | {current.CpuMax.ToString("F1", CultureInfo.InvariantCulture)}% | \u2014 |");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| Memory avg | {current.WorkingSetMBAvg.ToString("F1", CultureInfo.InvariantCulture)}MB | \u2014 |");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| Memory peak | {current.WorkingSetMBMax.ToString("F1", CultureInfo.InvariantCulture)}MB | {baselineMemPeak} |");
        sb.AppendLine();
    }

    private static void AppendRootCause(StringBuilder sb, RcaOptions options)
    {
        string scopeLabel = options.ChangeScope switch
        {
            OpportunityScope.Narrow => "narrow",
            OpportunityScope.Architecture => "architecture",
            _ => options.ChangeScope.ToString(),
        };

        sb.AppendLine("## Root Cause / Rationale");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Target file:** `{options.FilePath}`");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"**Scope:** `{scopeLabel}` \u2014 {options.ScopeReasoning}");
        sb.AppendLine();
        sb.AppendLine(options.Explanation);
        sb.AppendLine();
    }

    private static void AppendIterationSummary(StringBuilder sb, RcaOptions options)
    {
        if (!string.IsNullOrEmpty(options.IterationSummarySection))
        {
            sb.Append(options.IterationSummarySection);
            if (!options.IterationSummarySection.EndsWith('\n'))
            {
                sb.AppendLine();
            }

            sb.AppendLine();
        }
    }

    private static void AppendProposedFix(StringBuilder sb, RcaOptions options)
    {
        sb.AppendLine("## Proposed Fix");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(options.CodeBlock))
        {
            sb.AppendLine("```");
            sb.AppendLine(options.CodeBlock);
            sb.AppendLine("```");
        }
        else
        {
            sb.AppendLine("*No code block provided — see the experiment branch for the full diff.*");
        }
    }

    // ───── Helpers ─────

    private static string FormatDeltaCell(ComparisonResult? result, string metricName)
    {
        if (result is null)
        {
            return "N/A";
        }

        MetricComparison? metric = result.Details
            .FirstOrDefault(d =>
                string.Equals(d.MetricName, metricName, StringComparison.OrdinalIgnoreCase));

        if (metric is null)
        {
            return "N/A";
        }

        string sign = metric.DeltaPct >= 0 ? "+" : "";
        return sign + metric.DeltaPct.ToString("F1", CultureInfo.InvariantCulture) + "%";
    }

    private static string FormatPctDelta(double current, double baseline)
    {
        if (baseline == 0)
        {
            return "N/A";
        }

        double delta = (current - baseline) / baseline * 100;
        string sign = delta >= 0 ? "+" : "";
        return sign + delta.ToString("F1", CultureInfo.InvariantCulture) + "%";
    }
}
