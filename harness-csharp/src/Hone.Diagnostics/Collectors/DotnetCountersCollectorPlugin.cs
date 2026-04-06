using System.Globalization;
using System.Text.Json;

using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Diagnostics.Collectors;

/// <summary>
/// Collector plugin for dotnet-counters.
/// Collects runtime metrics via <c>dotnet-counters collect</c>.
/// </summary>
internal sealed class DotnetCountersCollectorPlugin : ICollectorPlugin
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly IProcessRunner _processRunner;

    public DotnetCountersCollectorPlugin(IProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        _processRunner = processRunner;
    }

    /// <inheritdoc />
    public string Name => "dotnet-counters";

    /// <inheritdoc />
    public async Task<CollectorStartResult> StartAsync(
        int processId,
        string outputDir,
        CollectorSettings settings,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        string outputPath = Path.Combine(outputDir, "dotnet-counters.csv");

        var args = new List<string>
        {
            "collect",
            "--process-id", processId.ToString(CultureInfo.InvariantCulture),
            "--output", outputPath,
            "--format", "csv",
        };

        int totalTimeout = settings.MaxCollectSec + settings.StopTimeoutSec + 60;
#pragma warning disable CA2000 // CTS ownership is transferred to DotnetCountersHandle/caller
        var collectionCts = new CancellationTokenSource();
#pragma warning restore CA2000

        try
        {
            Task<ProcessResult> collectionTask = _processRunner.RunAsync(
                "dotnet-counters",
                args,
                timeout: TimeSpan.FromSeconds(totalTimeout),
                ct: collectionCts.Token);

            // Brief startup delay
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

            if (collectionTask.IsCompleted)
            {
                collectionCts.Dispose();
                ProcessResult result = collectionTask.IsCompletedSuccessfully
                    ? await collectionTask.ConfigureAwait(false)
                    : new ProcessResult(Success: false, Output: "", ExitCode: -1, TimedOut: false);
                return new CollectorStartResult(Success: false,
                    Error: $"dotnet-counters exited prematurely with exit code {result.ExitCode}. " +
                           "Check dotnet tool install --global dotnet-counters.");
            }

#pragma warning disable CA2000 // Handle ownership is transferred to caller via CollectorStartResult.Handle
            var handle = new DotnetCountersHandle(collectionTask, collectionCts, outputPath);
#pragma warning restore CA2000
            return new CollectorStartResult(Success: true, Handle: handle);
        }
        catch
        {
            await collectionCts.CancelAsync().ConfigureAwait(false);
            collectionCts.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<CollectorArtifacts> StopAsync(
        object handle,
        CancellationToken ct = default)
    {
        if (handle is not DotnetCountersHandle dcHandle)
        {
            return new CollectorArtifacts(Success: false, ArtifactPaths: []);
        }

        try
        {
            // Cancel the dotnet-counters process — it flushes output on termination
            await dcHandle.CollectionCts.CancelAsync().ConfigureAwait(false);

            try
            {
                _ = await dcHandle.CollectionTask.WaitAsync(
                    TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected — we cancelled it
            }
            catch (TimeoutException)
            {
                // Process didn't respond to cancellation
            }

            // Allow file handles to flush
            await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);

            var artifactPaths = new List<string>();
            if (File.Exists(dcHandle.OutputPath))
            {
                artifactPaths.Add(dcHandle.OutputPath);
            }

            // Check for JSON file that may have been written alongside
            string jsonPath = Path.ChangeExtension(dcHandle.OutputPath, ".json");
            if (File.Exists(jsonPath))
            {
                artifactPaths.Add(jsonPath);
            }

            return new CollectorArtifacts(artifactPaths.Count > 0, artifactPaths);
        }
        finally
        {
            dcHandle.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<CollectorExportResult> ExportAsync(
        IReadOnlyList<string> artifactPaths,
        string outputDir,
        string processName,
        CollectorSettings settings,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);

        string? jsonSource = artifactPaths.FirstOrDefault(p =>
            p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(p));
        string? csvSource = artifactPaths.FirstOrDefault(p =>
            p.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) && File.Exists(p));

        var exportedPaths = new List<string>();
        JsonElement? metrics = null;

        // Prefer the pre-parsed JSON
        if (jsonSource is not null)
        {
            string destJson = Path.Combine(outputDir, Path.GetFileName(jsonSource));

            string resolvedSrc = Path.GetFullPath(jsonSource);
            string resolvedDest = Path.GetFullPath(destJson);

            if (!string.Equals(resolvedSrc, resolvedDest, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(jsonSource, destJson, overwrite: true);
            }

            exportedPaths.Add(destJson);

            string jsonContent = await PerfViewHelper.ReadFileWithSharingAsync(jsonSource, ct).ConfigureAwait(false);
            try
            {
                metrics = JsonSerializer.Deserialize<JsonElement>(jsonContent);
            }
            catch (JsonException)
            {
                // Invalid JSON — fall through to CSV parsing
            }
        }
        else if (csvSource is not null)
        {
            string csvContent = await PerfViewHelper.ReadFileWithSharingAsync(csvSource, ct).ConfigureAwait(false);
            JsonElement? parsed = ParseCountersCsv(csvContent);
            if (parsed.HasValue)
            {
                metrics = parsed;
                string destJson = Path.Combine(outputDir, "dotnet-counters.json");
                string json = JsonSerializer.Serialize(parsed.Value, IndentedJsonOptions);
                await File.WriteAllTextAsync(destJson, json, ct).ConfigureAwait(false);
                exportedPaths.Add(destJson);
            }
        }

        string summary = BuildSummary(metrics);

        return new CollectorExportResult(Success: true, ExportedPaths: exportedPaths, Summary: summary);
    }

    /// <summary>
    /// Builds a human-readable summary string from counter metrics.
    /// Format: "CPU avg: X% | GC heap max: Y MB | ..."
    /// </summary>
    internal static string BuildSummary(JsonElement? metrics)
    {
        if (!metrics.HasValue)
        {
            return "No dotnet-counters metrics available";
        }

        var parts = new List<string>();
        JsonElement root = metrics.Value;

        string cpuAvg = GetStatValue(root, "Runtime", "CpuUsage", "Avg", "%");
        string heapMax = GetStatValue(root, "Runtime", "GcHeapSizeMB", "Max", " MB");
        string gen2 = GetStatValue(root, "Runtime", "Gen2Collections", "Last", "");
        string gcPause = GetStatValue(root, "Runtime", "GcPauseRatio", "Max", "%");
        string threads = GetStatValue(root, "Runtime", "ThreadPoolThreads", "Max", "");
        string allocMB = GetStatValue(root, "Runtime", "AllocRateMB", "Avg", " MB/s");

        parts.Add($"CPU avg: {cpuAvg}");
        parts.Add($"GC heap max: {heapMax}");
        parts.Add($"Gen2 collections: {gen2}");
        parts.Add($"GC pause max: {gcPause}");
        parts.Add($"Thread pool max: {threads}");
        parts.Add($"Alloc rate avg: {allocMB}");

        return string.Join(" | ", parts);
    }

    private static string GetStatValue(
        JsonElement root,
        string section,
        string counter,
        string stat,
        string suffix)
    {
        try
        {
            if (root.TryGetProperty(section, out JsonElement sectionEl) &&
                sectionEl.TryGetProperty(counter, out JsonElement counterEl) &&
                counterEl.TryGetProperty(stat, out JsonElement statEl))
            {
                return statEl.GetDouble().ToString("G", CultureInfo.InvariantCulture) + suffix;
            }
        }
        catch (InvalidOperationException)
        {
            // JSON structure doesn't match expected format
        }

        return "N/A";
    }

    /// <summary>
    /// Parses dotnet-counters CSV into the structured JSON format.
    /// </summary>
    private static JsonElement? ParseCountersCsv(string csvContent)
    {
        string[] lines = csvContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            return null;
        }

        IReadOnlyList<string> header = FoldedStackParser.ParseCsvLine(lines[0].TrimEnd('\r'));
        int providerIndex = FindIndex(header, "Provider");
        int counterNameIndex = FindIndex(header, "Counter Name");
        int valueIndex = FindIndex(header, "Mean/Increment");

        if (providerIndex < 0 || counterNameIndex < 0 || valueIndex < 0)
        {
            return null;
        }

        var rows = new List<(string Provider, string CounterName, double Value)>();
        for (int i = 1; i < lines.Length; i++)
        {
            IReadOnlyList<string> fields = FoldedStackParser.ParseCsvLine(lines[i].TrimEnd('\r'));
            if (fields.Count <= Math.Max(Math.Max(providerIndex, counterNameIndex), valueIndex))
            {
                continue;
            }

            string provider = fields[providerIndex];
            string counterName = fields[counterNameIndex];
            string rawValue = fields[valueIndex].Trim();

            if (!string.IsNullOrEmpty(rawValue) &&
                double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                rows.Add((provider, counterName, value));
            }
        }

        if (rows.Count == 0)
        {
            return null;
        }

        // Build the metrics structure
        var metricsDict = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["TotalSamples"] = rows.Count,
            ["Runtime"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["CpuUsage"] = GetCounterStat(rows, "System.Runtime", "CPU Usage"),
                ["WorkingSetMB"] = GetCounterStat(rows, "System.Runtime", "Working Set"),
                ["GcHeapSizeMB"] = GetCounterStat(rows, "System.Runtime", "GC Heap Size"),
                ["Gen2Collections"] = GetCounterStat(rows, "System.Runtime", "Gen 2"),
                ["GcPauseRatio"] = GetCounterStat(rows, "System.Runtime", "time in GC"),
                ["AllocRateMB"] = GetCounterStat(rows, "System.Runtime", "Allocation Rate"),
                ["ExceptionCount"] = GetCounterStat(rows, "System.Runtime", "Exception"),
                ["ThreadPoolThreads"] = GetCounterStat(rows, "System.Runtime", "ThreadPool Thread"),
            },
            ["AspNetCore"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["RequestRate"] = GetCounterStat(rows, "Microsoft.AspNetCore.Hosting", "Request Rate"),
                ["FailedRequests"] = GetCounterStat(rows, "Microsoft.AspNetCore.Hosting", "Failed Requests"),
            },
        };

        using JsonDocument doc = JsonSerializer.SerializeToDocument(metricsDict);
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, double>? GetCounterStat(
        List<(string Provider, string CounterName, double Value)> rows,
        string provider,
        string counterSubstring)
    {
        double[] values =
        [
            .. rows
                .Where(r =>
                    string.Equals(r.Provider, provider, StringComparison.Ordinal) &&
                    r.CounterName.Contains(counterSubstring, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Value),
        ];

        if (values.Length == 0)
        {
            return null;
        }

        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["Avg"] = Math.Round(values.Average(), 2),
            ["Min"] = Math.Round(values.Min(), 2),
            ["Max"] = Math.Round(values.Max(), 2),
            ["Last"] = Math.Round(values[^1], 2),
            ["Samples"] = values.Length,
        };
    }

    private static int FindIndex(IReadOnlyList<string> header, string name)
    {
        for (int i = 0; i < header.Count; i++)
        {
            if (string.Equals(header[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
