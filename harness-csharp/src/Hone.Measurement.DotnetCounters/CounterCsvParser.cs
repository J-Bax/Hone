using System.Globalization;
using Hone.Measurement.Comparison;

namespace Hone.Measurement.DotnetCounters;

/// <summary>
/// Parses dotnet-counters CSV output into structured metrics.
/// </summary>
public static class CounterCsvParser
{
    private static readonly CounterStatistic Zero = new(0, 0, 0, 0, 0);

    // Maps a partial counter-name search string to the metric key used in the flattened dictionary.
    private static readonly (string CounterNamePart, string MetricKey)[] CounterMappings =
    [
        ("CPU Usage", "CpuUsage"),
        ("Working Set", "WorkingSetMB"),
        ("GC Heap Size", "GcHeapSizeMB"),
        ("Gen 0", "Gen0Collections"),
        ("Gen 1", "Gen1Collections"),
        ("Gen 2", "Gen2Collections"),
        ("time in GC", "GcPauseRatio"),
        ("Allocation Rate", "AllocRateMB"),
        ("Exception", "ExceptionCount"),
        ("ThreadPool Thread", "ThreadPoolThreads"),
        ("ThreadPool Queue", "ThreadPoolQueueLength"),
    ];

    /// <summary>
    /// Parses the CSV content and returns a flattened counter dictionary
    /// plus the full structured <see cref="RuntimeCounterMetrics"/>.
    /// </summary>
    public static CounterParseResult Parse(string csvContent)
    {
        if (string.IsNullOrWhiteSpace(csvContent))
        {
            return EmptyResult();
        }

        string[] lines = csvContent.Split('\n');

        // Locate the header row (first line starting with "Timestamp").
        int headerIdx = FindHeaderIndex(lines);
        if (headerIdx < 0 || headerIdx >= lines.Length - 1)
        {
            return EmptyResult();
        }

        string[] headers = lines[headerIdx].Trim().Split(',');
        int providerCol = FindColumn(headers, "Provider");
        int counterNameCol = FindColumn(headers, "Counter Name");
        int valueCol = FindColumn(headers, "Mean/Increment");

        if (providerCol < 0 || counterNameCol < 0 || valueCol < 0)
        {
            return EmptyResult();
        }

        // Parse data rows.
        int minColumns = Math.Max(providerCol, Math.Max(counterNameCol, valueCol)) + 1;
        var rows = new List<CsvRow>();

        for (int i = headerIdx + 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Split(',');
            if (parts.Length < minColumns)
            {
                continue;
            }

            if (double.TryParse(
                    parts[valueCol].Trim(),
                    NumberStyles.Float | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out double value))
            {
                rows.Add(new CsvRow(parts[providerCol].Trim(), parts[counterNameCol].Trim(), value));
            }
        }

        if (rows.Count == 0)
        {
            return EmptyResult();
        }

        // Compute a CounterStatistic for each known metric.
        var stats = new Dictionary<string, CounterStatistic>(StringComparer.Ordinal);
        foreach ((string counterNamePart, string metricKey) in CounterMappings)
        {
            stats[metricKey] = ComputeStatistic(rows, "System.Runtime", counterNamePart);
        }

        var structured = new RuntimeCounterMetrics(
            CpuUsage: stats["CpuUsage"],
            WorkingSetMB: stats["WorkingSetMB"],
            GcHeapSizeMB: stats["GcHeapSizeMB"],
            Gen0Collections: stats["Gen0Collections"],
            Gen1Collections: stats["Gen1Collections"],
            Gen2Collections: stats["Gen2Collections"],
            GcPauseRatio: stats["GcPauseRatio"],
            ThreadPoolThreads: stats["ThreadPoolThreads"],
            ThreadPoolQueueLength: stats["ThreadPoolQueueLength"],
            ExceptionCount: stats["ExceptionCount"],
            AllocRateMB: stats["AllocRateMB"]);

        // Flatten into key→value dictionary.
        var counters = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((string metricKey, CounterStatistic stat) in stats)
        {
            counters[$"{metricKey}.Avg"] = stat.Avg;
            counters[$"{metricKey}.Min"] = stat.Min;
            counters[$"{metricKey}.Max"] = stat.Max;
            counters[$"{metricKey}.Last"] = stat.Last;
            counters[$"{metricKey}.Samples"] = stat.Samples;
        }

        return new CounterParseResult(counters, structured);
    }

    private static CounterParseResult EmptyResult() =>
        new(Counters: new Dictionary<string, double>(StringComparer.Ordinal), StructuredMetrics: null);

    private static int FindHeaderIndex(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("Timestamp", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindColumn(string[] headers, string name)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            if (headers[i].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Filters rows by exact provider match and partial counter-name match,
    /// then computes aggregate statistics — mirroring the PowerShell
    /// <c>Get-CounterStat</c> helper.
    /// </summary>
    private static CounterStatistic ComputeStatistic(
        List<CsvRow> rows,
        string provider,
        string counterNamePart)
    {
        var values = rows
            .Where(r => r.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)
                     && r.CounterName.Contains(counterNamePart, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Value)
            .ToList();

        if (values.Count == 0)
        {
            return Zero;
        }

        return new CounterStatistic(
            Avg: values.Average(),
            Min: values.Min(),
            Max: values.Max(),
            Last: values[^1],
            Samples: values.Count);
    }

    private sealed record CsvRow(string Provider, string CounterName, double Value);
}
