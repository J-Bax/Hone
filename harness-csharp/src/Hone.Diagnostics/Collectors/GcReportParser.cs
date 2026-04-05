using System.Globalization;
using System.Text.RegularExpressions;

namespace Hone.Diagnostics.Collectors;

/// <summary>
/// Parses PerfView's GCStats HTML output into a structured <see cref="GcReport"/>.
/// </summary>
internal static partial class GcReportParser
{
    /// <summary>
    /// Parses the GCStats HTML content and extracts per-generation stats,
    /// heap stats, pause stats, and allocation stats for the given process.
    /// </summary>
    public static GcReport Parse(string htmlContent, string processName)
    {
        var report = new GcReport();

        if (string.IsNullOrWhiteSpace(htmlContent))
        {
            return report;
        }

        // Scope to the target process section (between HR tags)
        string processSection = FindProcessSection(htmlContent, processName);

        ParseGenerationTable(processSection, report);
        ParseSummaryStats(processSection, report);

        return report;
    }

    /// <summary>
    /// Finds the HTML section for the target process, scoped by HR tags.
    /// Falls back to the full HTML if no matching section is found.
    /// </summary>
    private static string FindProcessSection(string htmlContent, string processName)
    {
        string[] hrBlocks = HrSplitRegex().Split(htmlContent);

        // Try to find a block matching the process name with GC Rollup data
        string escapedName = Regex.Escape(processName);
        foreach (string block in hrBlocks)
        {
            if (Regex.IsMatch(block, escapedName, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) &&
                block.Contains("GC Rollup", StringComparison.OrdinalIgnoreCase))
            {
                return block;
            }
        }

        // Fall back to any block with GC Rollup and process stats
        foreach (string block in hrBlocks)
        {
            if (block.Contains("GC Rollup", StringComparison.OrdinalIgnoreCase) &&
                block.Contains("GC Stats for Process", StringComparison.OrdinalIgnoreCase))
            {
                return block;
            }
        }

        return htmlContent;
    }

    /// <summary>
    /// Parses the GC Rollup table rows to extract per-generation statistics.
    /// Table columns: Gen, Count, MaxPause, MaxPeakMB, MaxAllocMBSec,
    ///                TotalPause, TotalAllocMB, ..., MeanPause, Induced
    /// </summary>
    private static void ParseGenerationTable(string html, GcReport report)
    {
        MatchCollection tableRows = TableRowRegex().Matches(html);

        foreach (Match tr in tableRows)
        {
            MatchCollection cells = TableCellRegex().Matches(tr.Groups["content"].Value);
            if (cells.Count < 10)
            {
                continue;
            }

            string genVal = StripHtmlTags(cells[0].Groups["content"].Value).Trim();
            string countRaw = CleanNumeric(cells[1].Groups["content"].Value);
            string maxPauseRaw = CleanNumeric(cells[2].Groups["content"].Value);
            string meanPauseRaw = CleanNumeric(cells[9].Groups["content"].Value);

            if (genVal is "0" or "1" or "2")
            {
                GenerationInfo gen = genVal switch
                {
                    "0" => report.GenerationStats.Gen0,
                    "1" => report.GenerationStats.Gen1,
                    "2" => report.GenerationStats.Gen2,
                    _ => report.GenerationStats.Gen0,
                };

                if (int.TryParse(countRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                {
                    gen.Count = count;
                }

                if (TryParseNonNaN(maxPauseRaw, out double maxPause))
                {
                    gen.MaxPauseMs = Math.Round(maxPause, 3);
                }

                if (TryParseNonNaN(meanPauseRaw, out double meanPause))
                {
                    gen.AvgPauseMs = Math.Round(meanPause, 3);
                }
            }
            else if (string.Equals(genVal, "ALL", StringComparison.Ordinal))
            {
                if (TryParseNonNaN(maxPauseRaw, out double allMaxPause))
                {
                    report.PauseStats.MaxPauseMs = Math.Round(allMaxPause, 3);
                }
            }
        }
    }

    /// <summary>
    /// Parses summary stats from LI items in the HTML.
    /// </summary>
    private static void ParseSummaryStats(string html, GcReport report)
    {
        double? totalPause = FindMetric(html, TotalPauseRegex());
        if (totalPause.HasValue)
        {
            report.PauseStats.TotalPauseMs = Math.Round(totalPause.Value, 3);
        }

        double? gcRatio = FindMetric(html, GcPauseRatioRegex());
        if (gcRatio.HasValue)
        {
            report.PauseStats.GcPauseRatio = Math.Round(gcRatio.Value, 2);
        }

        double? peakHeap = FindMetric(html, PeakHeapRegex());
        if (peakHeap.HasValue)
        {
            report.HeapStats.PeakSizeMB = Math.Round(peakHeap.Value, 2);
        }

        double? totalAlloc = FindMetric(html, TotalAllocRegex());
        if (totalAlloc.HasValue)
        {
            report.HeapStats.TotalAllocMB = Math.Round(totalAlloc.Value, 2);
        }

        double? allocRate = FindMetric(html, AllocRateRegex());
        if (allocRate.HasValue)
        {
            report.AllocationStats.AllocRateMBSec = Math.Round(allocRate.Value, 2);
        }
    }

    private static double? FindMetric(string html, Regex pattern)
    {
        Match match = pattern.Match(html);
        if (!match.Success)
        {
            return null;
        }

        string raw = match.Groups["value"].Value.Replace(",", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal);

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    private static string StripHtmlTags(string input) =>
        HtmlTagRegex().Replace(input, "");

    private static string CleanNumeric(string input) =>
        StripHtmlTags(input).Trim()
            .Replace(",", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal);

    private static bool TryParseNonNaN(string raw, out double value)
    {
        value = 0;
        if (string.Equals(raw, "NaN", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // ── Generated regex patterns ────────────────────────────────────────

    [GeneratedRegex(@"<HR\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex HrSplitRegex();

    [GeneratedRegex(@"<TR[^>]*>(?<content>.*?)</TR>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture)]
    private static partial Regex TableRowRegex();

    [GeneratedRegex(@"<TD[^>]*>(?<content>.*?)</TD>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture)]
    private static partial Regex TableCellRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.NonBacktracking)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"Total\s+GC\s+Pause:\s*(?<value>[\d,.]+)\s*msec", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture)]
    private static partial Regex TotalPauseRegex();

    [GeneratedRegex(@"%\s*Time\s+paused\s+for\s+Garbage\s+Collection:\s*(?<value>[\d,.]+)%", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture)]
    private static partial Regex GcPauseRatioRegex();

    [GeneratedRegex(@"Max\s+GC\s+Heap\s+Size:\s*(?<value>[\d,.]+)\s*MB", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture)]
    private static partial Regex PeakHeapRegex();

    [GeneratedRegex(@"Total\s+Allocs\s*:\s*(?<value>[\d,.]+)\s*MB", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture)]
    private static partial Regex TotalAllocRegex();

    [GeneratedRegex(@"Alloc.*?Rate.*?(?<value>[\d,.]+)\s*MB/sec", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture)]
    private static partial Regex AllocRateRegex();
}
