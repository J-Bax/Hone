using System.Globalization;

using Hone.Core.Models;

namespace Hone.Reporting.Console;

/// <summary>
/// Renders performance results as a formatted console table.
/// Pure renderer — no file I/O.
/// </summary>
public static class ResultsRenderer
{
    private const int LabelWidth = 14;
    private const int ColWidth = 12;
    private const int TotalWidth = LabelWidth + (ColWidth * 7) + 2;
    private const int BarMaxLength = 50;
    private const string Dash = "\u2014";

    // Scenario breakdown column widths
    private const int ScenarioNameWidth = 22;
    private const int ScenarioColWidth = 12;

    /// <summary>
    /// Renders the complete results view to the provided writer.
    /// </summary>
    public static void Render(ResultsViewModel model, IConsoleColorWriter writer)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(writer);

        RenderBanner(writer);
        RenderMachineInfo(model, writer);
        RenderToleranceInfo(model, writer);
        RenderMainTable(model, writer);
        RenderScenarioBreakdown(model, writer);
        RenderLatencyDistribution(model, writer);
        RenderOverallStatus(model, writer);
    }

    // ───── Banner ─────

    private static void RenderBanner(IConsoleColorWriter writer)
    {
        string border = new('\u2550', 70);

        writer.WriteLine();
        writer.WriteLine("  " + border, ConsoleColor.DarkCyan);
        writer.WriteLine("                      HONE PERFORMANCE RESULTS", ConsoleColor.DarkCyan);
        writer.WriteLine("  " + border, ConsoleColor.DarkCyan);
        writer.WriteLine();
    }

    // ───── Machine Info ─────

    private static void RenderMachineInfo(ResultsViewModel model, IConsoleColorWriter writer)
    {
        MachineInfo? machine = model.Metadata?.MachineInfo;
        if (machine is null)
        {
            return;
        }

        if (machine.CpuName is not null)
        {
            writer.Write("  CPU:     ", ConsoleColor.DarkGray);
            string cpuText = machine.CpuCores.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $"{machine.CpuName} ({machine.CpuCores} cores)")
                : machine.CpuName;
            writer.Write(cpuText, ConsoleColor.White);

            if (machine.TotalRamGB.HasValue)
            {
                writer.Write(" \u00b7 ", ConsoleColor.DarkGray);
                writer.WriteLine(
                    string.Create(CultureInfo.InvariantCulture, $"RAM: {machine.TotalRamGB}GB"),
                    ConsoleColor.White);
            }
            else
            {
                writer.WriteLine();
            }
        }

        if (machine.OsVersion is not null)
        {
            writer.Write("  OS:      ", ConsoleColor.DarkGray);
            writer.WriteLine(machine.OsVersion, ConsoleColor.White);
        }

        if (machine.DotnetVersion is not null)
        {
            writer.Write("  Runtime: ", ConsoleColor.DarkGray);
            writer.WriteLine(
                string.Create(CultureInfo.InvariantCulture, $".NET {machine.DotnetVersion}"),
                ConsoleColor.White);
        }

        if (model.Metadata?.StartedAt is not null)
        {
            writer.Write("  Started: ", ConsoleColor.DarkGray);
            writer.WriteLine(model.Metadata.StartedAt, ConsoleColor.White);
        }

        writer.WriteLine();
    }

    // ───── Tolerance Info ─────

    private static void RenderToleranceInfo(ResultsViewModel model, IConsoleColorWriter writer)
    {
        double minImprove = Math.Round(model.Tolerances.MinImprovementPct * 100, 1);
        double maxRegress = Math.Round(model.Tolerances.MaxRegressionPct * 100, 1);

        writer.Write("  Mode: ", ConsoleColor.DarkGray);
        writer.Write("Relative improvement", ConsoleColor.White);
        writer.Write(" \u00b7 ", ConsoleColor.DarkGray);
        writer.Write(
            string.Create(CultureInfo.InvariantCulture, $"Min improvement: {minImprove}%"),
            ConsoleColor.White);
        writer.Write(" \u00b7 ", ConsoleColor.DarkGray);
        writer.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"Max regression: {maxRegress}%"),
            ConsoleColor.White);
        writer.WriteLine();
    }

    // ───── Main Table ─────

    private static void RenderMainTable(ResultsViewModel model, IConsoleColorWriter writer)
    {
        string separator = "  " + new string('\u2500', TotalWidth);

        // Header row
        writer.Write("  ");
        writer.Write(Pad("Experiment", -LabelWidth), ConsoleColor.Cyan);
        writer.Write(Pad("p95 (ms)", ColWidth), ConsoleColor.Cyan);
        writer.Write(Pad("Avg (ms)", ColWidth), ConsoleColor.Cyan);
        writer.Write(Pad("RPS", ColWidth), ConsoleColor.Cyan);
        writer.Write(Pad("Error %", ColWidth), ConsoleColor.Cyan);
        writer.Write(Pad("p95 \u0394", ColWidth), ConsoleColor.Cyan);
        writer.Write(Pad("CPU avg%", ColWidth), ConsoleColor.Cyan);
        writer.WriteLine(Pad("Mem MB", ColWidth), ConsoleColor.Cyan);
        writer.WriteLine(separator, ConsoleColor.DarkGray);

        // Baseline row
        double bP95 = Math.Round(model.Baseline.HttpReqDuration.P95, 2);
        double bAvg = Math.Round(model.Baseline.HttpReqDuration.Avg, 2);
        double bRps = Math.Round(model.Baseline.HttpReqs.Rate, 1);
        string bErr = FormatPct(model.Baseline.HttpReqFailed.Rate);
        string bCpu = FormatCounter(model.BaselineCounters?.CpuAvgPercent);
        string bMem = FormatCounter(model.BaselineCounters?.MemoryMB);

        writer.Write("  ");
        writer.Write(Pad("Baseline", -LabelWidth), ConsoleColor.Yellow);
        writer.Write(Pad(FormatNum(bP95), ColWidth), ConsoleColor.White);
        writer.Write(Pad(FormatNum(bAvg), ColWidth), ConsoleColor.White);
        writer.Write(Pad(FormatNum(bRps), ColWidth), ConsoleColor.White);
        writer.Write(Pad(bErr, ColWidth), ConsoleColor.White);
        writer.Write(Pad(Dash, ColWidth), ConsoleColor.DarkGray);
        writer.Write(Pad(bCpu, ColWidth), ConsoleColor.White);
        writer.WriteLine(Pad(bMem, ColWidth), ConsoleColor.White);
        writer.WriteLine(separator, ConsoleColor.DarkGray);

        // Experiment rows
        foreach (ExperimentRow exp in model.Experiments)
        {
            if (exp.Experiment == 0)
            {
                continue;
            }

            double iP95 = Math.Round(exp.Metrics.HttpReqDuration.P95, 2);
            double iAvg = Math.Round(exp.Metrics.HttpReqDuration.Avg, 2);
            double iRps = Math.Round(exp.Metrics.HttpReqs.Rate, 1);
            string iErr = FormatPct(exp.Metrics.HttpReqFailed.Rate);
            DeltaValue deltaP95 = FormatDelta(iP95, bP95, lowerIsBetter: true);

            ConsoleColor p95Color = CompareColor(iP95, bP95, lowerIsBetter: true);
            ConsoleColor rpsColor = CompareColor(iRps, bRps, lowerIsBetter: false);
            ConsoleColor errColor = CompareColor(
                exp.Metrics.HttpReqFailed.Rate,
                model.Baseline.HttpReqFailed.Rate,
                lowerIsBetter: true);

            string iCpu = FormatCounter(exp.Counters?.CpuAvgPercent);
            string iMem = FormatCounter(exp.Counters?.MemoryMB);
            ConsoleColor cpuColor = CounterColor(exp.Counters?.CpuAvgPercent, model.BaselineCounters?.CpuAvgPercent);
            ConsoleColor memColor = CounterColor(exp.Counters?.MemoryMB, model.BaselineCounters?.MemoryMB);

            string label = string.Create(CultureInfo.InvariantCulture, $"Experiment {exp.Experiment}");

            writer.Write("  ");
            writer.Write(Pad(label, -LabelWidth), ConsoleColor.White);
            writer.Write(Pad(FormatNum(iP95), ColWidth), p95Color);
            writer.Write(Pad(FormatNum(iAvg), ColWidth), ConsoleColor.White);
            writer.Write(Pad(FormatNum(iRps), ColWidth), rpsColor);
            writer.Write(Pad(iErr, ColWidth), errColor);
            writer.Write(Pad(deltaP95.Text, ColWidth), deltaP95.Color);
            writer.Write(Pad(iCpu, ColWidth), cpuColor);
            writer.WriteLine(Pad(iMem, ColWidth), memColor);
        }

        if (model.Experiments.Count == 0)
        {
            writer.Write("  ");
            writer.WriteLine("  No optimization experiments yet.", ConsoleColor.DarkGray);
        }
    }

    // ───── Scenario Breakdown ─────

    private static void RenderScenarioBreakdown(ResultsViewModel model, IConsoleColorWriter writer)
    {
        if (model.Scenarios is null || model.Scenarios.Count == 0)
        {
            return;
        }

        writer.WriteLine();
        writer.WriteLine(
            "  " + new string('\u2500', 2) + " Scenario Breakdown " + new string('\u2500', 54),
            ConsoleColor.DarkCyan);
        writer.WriteLine();

        foreach (ScenarioResult scenario in model.Scenarios)
        {
            writer.Write("  ");
            writer.WriteLine(scenario.ScenarioName, ConsoleColor.Yellow);

            // Sub-header
            writer.Write("  ");
            writer.Write(Pad("", -ScenarioNameWidth));
            writer.Write(Pad("p95 (ms)", ScenarioColWidth), ConsoleColor.DarkGray);
            writer.Write(Pad("RPS", ScenarioColWidth), ConsoleColor.DarkGray);
            writer.Write(Pad("Error %", ScenarioColWidth), ConsoleColor.DarkGray);
            writer.WriteLine(Pad("p95 \u0394", ScenarioColWidth), ConsoleColor.DarkGray);

            // Scenario baseline row
            double sbP95 = Math.Round(scenario.Baseline.Metrics.HttpReqDuration.P95, 2);
            double sbRps = Math.Round(scenario.Baseline.Metrics.HttpReqs.Rate, 1);
            string sbErr = FormatPct(scenario.Baseline.Metrics.HttpReqFailed.Rate);

            writer.Write("  ");
            writer.Write(Pad("  Baseline", -ScenarioNameWidth), ConsoleColor.DarkGray);
            writer.Write(Pad(FormatNum(sbP95), ScenarioColWidth), ConsoleColor.White);
            writer.Write(Pad(FormatNum(sbRps), ScenarioColWidth), ConsoleColor.White);
            writer.Write(Pad(sbErr, ScenarioColWidth), ConsoleColor.White);
            writer.WriteLine(Pad(Dash, ScenarioColWidth), ConsoleColor.DarkGray);

            // Scenario experiment rows
            int scenarioExpCount = 0;
            foreach (ExperimentRow exp in scenario.Experiments)
            {
                if (exp.Experiment == 0)
                {
                    continue;
                }

                scenarioExpCount++;
                double sP95 = Math.Round(exp.Metrics.HttpReqDuration.P95, 2);
                double sRps = Math.Round(exp.Metrics.HttpReqs.Rate, 1);
                string sErr = FormatPct(exp.Metrics.HttpReqFailed.Rate);
                DeltaValue sDelta = FormatDelta(sP95, sbP95, lowerIsBetter: true);
                ConsoleColor sP95Color = CompareColor(sP95, sbP95, lowerIsBetter: true);

                string label = string.Create(CultureInfo.InvariantCulture, $"  Experiment {exp.Experiment}");

                writer.Write("  ");
                writer.Write(Pad(label, -ScenarioNameWidth), ConsoleColor.White);
                writer.Write(Pad(FormatNum(sP95), ScenarioColWidth), sP95Color);
                writer.Write(Pad(FormatNum(sRps), ScenarioColWidth), ConsoleColor.White);
                writer.Write(Pad(sErr, ScenarioColWidth), ConsoleColor.White);
                writer.WriteLine(Pad(sDelta.Text, ScenarioColWidth), sDelta.Color);
            }

            if (scenarioExpCount == 0)
            {
                writer.Write("  ");
                writer.WriteLine("    No experiment data yet", ConsoleColor.DarkGray);
            }

            writer.WriteLine();
        }
    }

    // ───── Latency Distribution ─────

    private static void RenderLatencyDistribution(ResultsViewModel model, IConsoleColorWriter writer)
    {
        // Baseline latency bars
        writer.WriteLine();
        writer.WriteLine("  Latency Distribution (Baseline):", ConsoleColor.Cyan);
        writer.WriteLine();

        HttpReqDurationMetrics bDuration = model.Baseline.HttpReqDuration;
        double baselineMax = MaxOf(bDuration.P50, bDuration.P90, bDuration.P95, bDuration.Max);

        RenderPercentileBars(writer, bDuration, baselineMax, baseline: null);

        // Latest experiment latency bars
        ExperimentRow? latest = FindLatestExperiment(model.Experiments);
        if (latest is not null)
        {
            writer.WriteLine();
            writer.WriteLine(
                string.Create(CultureInfo.InvariantCulture,
                    $"  Latency Distribution (Experiment {latest.Experiment}):"),
                ConsoleColor.Cyan);
            writer.WriteLine();

            HttpReqDurationMetrics eDuration = latest.Metrics.HttpReqDuration;
            double expMax = MaxOf(eDuration.P50, eDuration.P90, eDuration.P95, eDuration.Max);
            double scaleMax = Math.Max(baselineMax, expMax);

            RenderPercentileBars(writer, eDuration, scaleMax, bDuration);
        }
    }

    private static void RenderPercentileBars(
        IConsoleColorWriter writer,
        HttpReqDurationMetrics duration,
        double scaleMax,
        HttpReqDurationMetrics? baseline)
    {
        ReadOnlySpan<(string Label, double Value, double BaseValue)> percentiles =
        [
            ("p50", duration.P50, baseline?.P50 ?? 0),
            ("p90", duration.P90, baseline?.P90 ?? 0),
            ("p95", duration.P95, baseline?.P95 ?? 0),
            ("Max", duration.Max, baseline?.Max ?? 0),
        ];

        foreach ((string label, double value, double baseValue) in percentiles)
        {
            if (value <= 0)
            {
                continue;
            }

            int barLen = scaleMax > 0
                ? Math.Max(1, (int)Math.Round(value / scaleMax * BarMaxLength))
                : 1;
            string bar = new('\u2588', barLen);

            ConsoleColor color;
            if (baseline is null)
            {
                color = ConsoleColor.Cyan;
            }
            else if (value < baseValue)
            {
                color = ConsoleColor.Green;
            }
            else if (value > baseValue)
            {
                color = ConsoleColor.Red;
            }
            else
            {
                color = ConsoleColor.Cyan;
            }

            string valueText = Math.Round(value, 1)
                .ToString(CultureInfo.InvariantCulture);

            writer.Write(string.Format(CultureInfo.InvariantCulture, "  {0,4}  ", label), ConsoleColor.DarkGray);
            writer.Write(bar, color);
            writer.WriteLine(
                string.Create(CultureInfo.InvariantCulture, $" {valueText}ms"),
                ConsoleColor.White);
        }
    }

    // ───── Overall Status ─────

    private static void RenderOverallStatus(ResultsViewModel model, IConsoleColorWriter writer)
    {
        writer.WriteLine();

        ExperimentRow? latest = FindLatestExperiment(model.Experiments);

        writer.Write("  Status: ", ConsoleColor.DarkGray);

        if (latest is not null)
        {
            HttpReqDurationMetrics latestDuration = latest.Metrics.HttpReqDuration;
            HttpReqDurationMetrics baselineDuration = model.Baseline.HttpReqDuration;

            bool improved =
                (latestDuration.P95 < baselineDuration.P95) ||
                (latest.Metrics.HttpReqs.Rate > model.Baseline.HttpReqs.Rate) ||
                (latest.Metrics.HttpReqFailed.Rate < model.Baseline.HttpReqFailed.Rate);

            if (improved)
            {
                DeltaValue p95Delta = FormatDelta(latestDuration.P95, baselineDuration.P95, lowerIsBetter: true);
                DeltaValue rpsDelta = FormatDelta(latest.Metrics.HttpReqs.Rate, model.Baseline.HttpReqs.Rate, lowerIsBetter: false);

                writer.Write("IMPROVED vs BASELINE", ConsoleColor.Green);
                writer.Write(
                    string.Create(CultureInfo.InvariantCulture, $" | p95 {p95Delta.Text}"),
                    p95Delta.Color);
                writer.WriteLine(
                    string.Create(CultureInfo.InvariantCulture, $" | RPS {rpsDelta.Text}"),
                    rpsDelta.Color);
            }
            else
            {
                writer.WriteLine("NO NET IMPROVEMENT vs BASELINE", ConsoleColor.Yellow);
            }

            // Efficiency summary
            RenderEfficiencySummary(model, latest, writer);
        }
        else
        {
            writer.WriteLine(
                "Baseline only \u2014 run optimization loop to optimize",
                ConsoleColor.DarkGray);
        }

        writer.WriteLine();
    }

    private static void RenderEfficiencySummary(
        ResultsViewModel model,
        ExperimentRow latest,
        IConsoleColorWriter writer)
    {
        if (latest.Counters is null || model.BaselineCounters is null)
        {
            return;
        }

        if (!latest.Counters.CpuAvgPercent.HasValue || !model.BaselineCounters.CpuAvgPercent.HasValue)
        {
            return;
        }

        DeltaValue cpuDelta = FormatDelta(
            latest.Counters.CpuAvgPercent.Value,
            model.BaselineCounters.CpuAvgPercent.Value,
            lowerIsBetter: true);

        writer.Write("  Efficiency: ", ConsoleColor.DarkGray);
        writer.Write(
            string.Create(CultureInfo.InvariantCulture, $"CPU {cpuDelta.Text}"),
            cpuDelta.Color);

        if (latest.Counters.MemoryMB.HasValue && model.BaselineCounters.MemoryMB.HasValue)
        {
            DeltaValue memDelta = FormatDelta(
                latest.Counters.MemoryMB.Value,
                model.BaselineCounters.MemoryMB.Value,
                lowerIsBetter: true);

            writer.Write(" | ", ConsoleColor.DarkGray);
            writer.WriteLine(
                string.Create(CultureInfo.InvariantCulture, $"Memory {memDelta.Text}"),
                memDelta.Color);
        }
        else
        {
            writer.WriteLine();
        }
    }

    // ───── Helpers ─────

    private static string Pad(string value, int width)
    {
        return width < 0
            ? value.PadRight(-width)
            : value.PadLeft(width);
    }

    private static string FormatNum(double value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string FormatPct(double rate)
    {
        double pct = Math.Round(rate * 100, 2);
        return pct.ToString(CultureInfo.InvariantCulture) + "%";
    }

    private static string FormatCounter(double? value) =>
        value.HasValue
            ? Math.Round(value.Value, 1).ToString(CultureInfo.InvariantCulture)
            : Dash;

    private static DeltaValue FormatDelta(double current, double baseline, bool lowerIsBetter)
    {
        if (baseline == 0)
        {
            return new DeltaValue("N/A", ConsoleColor.Gray);
        }

        double pct = Math.Round((current - baseline) / baseline * 100, 1);
        string sign = pct > 0 ? "+" : "";
        string text = string.Create(CultureInfo.InvariantCulture, $"{sign}{pct}%");

        bool improved = lowerIsBetter ? pct < 0 : pct > 0;
        ConsoleColor color = improved
            ? ConsoleColor.Green
            : pct == 0
                ? ConsoleColor.Gray
                : ConsoleColor.Red;

        return new DeltaValue(text, color);
    }

    private static ConsoleColor CompareColor(double current, double baseline, bool lowerIsBetter)
    {
        if (current < baseline)
        {
            return lowerIsBetter ? ConsoleColor.Green : ConsoleColor.Red;
        }

        if (current > baseline)
        {
            return lowerIsBetter ? ConsoleColor.Red : ConsoleColor.Green;
        }

        return ConsoleColor.White;
    }

    private static ConsoleColor CounterColor(double? current, double? baseline)
    {
        if (!current.HasValue || !baseline.HasValue)
        {
            return ConsoleColor.White;
        }

        if (current.Value < baseline.Value)
        {
            return ConsoleColor.Green;
        }

        return current.Value > baseline.Value ? ConsoleColor.Red : ConsoleColor.White;
    }

    private static ExperimentRow? FindLatestExperiment(IReadOnlyList<ExperimentRow> experiments) =>
        experiments.Where(e => e.Experiment > 0).MaxBy(e => e.Experiment);

    private static double MaxOf(double a, double b, double c, double d) =>
        Math.Max(Math.Max(a, b), Math.Max(c, d));

    private readonly record struct DeltaValue(string Text, ConsoleColor Color);
}
