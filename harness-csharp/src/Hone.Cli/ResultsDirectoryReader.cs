using System.Globalization;
using System.Text.Json;
using Hone.Core.Models;
using Hone.Measurement.K6;
using Hone.Reporting.Console;

namespace Hone.Cli;

/// <summary>
/// Snapshot of all result data loaded from the <c>.hone/results</c> directory.
/// </summary>
internal sealed record ResultsSnapshot(
    MetricSet Baseline,
    IReadOnlyList<ExperimentRow> Experiments,
    RunMetadata? Metadata,
    ConsoleCounterData? BaselineCounters,
    IReadOnlyList<ScenarioResult> Scenarios);

/// <summary>
/// Reads the <c>.hone/results</c> directory and assembles a <see cref="ResultsSnapshot"/>
/// that can be fed to <see cref="Hone.Reporting.Console.ResultsRenderer"/>
/// or <see cref="Hone.Reporting.Dashboard.DashboardExporter"/>.
/// </summary>
internal static class ResultsDirectoryReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Loads all result data from the given results directory.
    /// </summary>
    public static async Task<ResultsSnapshot> LoadAsync(
        string resultsPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(resultsPath);

        if (!Directory.Exists(resultsPath))
        {
            throw new DirectoryNotFoundException(
                $"Results directory not found: {resultsPath}");
        }

        // 1. Load baseline
        MetricSet baseline = await LoadBaselineAsync(resultsPath, ct).ConfigureAwait(false);

        // 2. Load experiments
        List<(int Number, MetricSet Metrics)> experiments = await LoadExperimentsAsync(
            resultsPath, ct).ConfigureAwait(false);

        // 3. Load counter data per experiment
        List<ExperimentRow> rows = [];
        foreach ((int number, MetricSet metrics) in experiments)
        {
            ConsoleCounterData? counters = await LoadExperimentCountersAsync(
                resultsPath, number, ct).ConfigureAwait(false);
            rows.Add(new ExperimentRow(number, metrics, counters));
        }

        // 4. Load run metadata
        RunMetadata? metadata = await LoadRunMetadataAsync(resultsPath, ct).ConfigureAwait(false);

        // 5. Load baseline counters
        ConsoleCounterData? baselineCounters = await LoadBaselineCountersAsync(
            resultsPath, ct).ConfigureAwait(false);

        // 6. Load scenario data
        List<ScenarioResult> scenarios = await LoadScenariosAsync(
            resultsPath, experiments, ct).ConfigureAwait(false);

        return new ResultsSnapshot(baseline, rows, metadata, baselineCounters, scenarios);
    }

    // ── Baseline ──────────────────────────────────────────────────────────

    private static async Task<MetricSet> LoadBaselineAsync(string resultsPath, CancellationToken ct)
    {
        // Try baseline/k6-summary.json first (new layout), then baseline.json (legacy)
        string baselineSummary = Path.Combine(resultsPath, "baseline", "k6-summary.json");
        if (File.Exists(baselineSummary))
        {
            return await K6SummaryParser.ParseAsync(baselineSummary, experiment: 0, run: 0, ct)
                .ConfigureAwait(false);
        }

        string baselineJson = Path.Combine(resultsPath, "baseline.json");
        if (File.Exists(baselineJson))
        {
            return await K6SummaryParser.ParseAsync(baselineJson, experiment: 0, run: 0, ct)
                .ConfigureAwait(false);
        }

        throw new FileNotFoundException(
            $"No baseline file found. Expected '{baselineSummary}' or '{baselineJson}'.");
    }

    // ── Experiments ───────────────────────────────────────────────────────

    private static async Task<List<(int Number, MetricSet Metrics)>> LoadExperimentsAsync(
        string resultsPath, CancellationToken ct)
    {
        var results = new List<(int Number, MetricSet Metrics)>();

        if (!Directory.Exists(resultsPath))
        {
            return results;
        }

        foreach (string dir in Directory.GetDirectories(resultsPath, "experiment-*"))
        {
            string dirName = Path.GetFileName(dir);
            if (!int.TryParse(dirName.AsSpan("experiment-".Length), NumberStyles.None, CultureInfo.InvariantCulture, out int experimentNum))
            {
                continue;
            }

            // Try k6-summary.json (primary), then k6-summary-run0.json
            string summaryPath = Path.Combine(dir, "k6-summary.json");
            if (!File.Exists(summaryPath))
            {
                summaryPath = Path.Combine(dir, "k6-summary-run0.json");
            }

            if (!File.Exists(summaryPath))
            {
                continue;
            }

            MetricSet metrics = await K6SummaryParser.ParseAsync(
                summaryPath, experimentNum, run: 0, ct).ConfigureAwait(false);
            results.Add((experimentNum, metrics));
        }

        results.Sort((a, b) => a.Number.CompareTo(b.Number));
        return results;
    }

    // ── Counter data ──────────────────────────────────────────────────────

    private static async Task<ConsoleCounterData?> LoadExperimentCountersAsync(
        string resultsPath, int experiment, CancellationToken ct)
    {
        // Try diagnostics/dotnet-counters/dotnet-counters.json first, then direct
        string diagnosticsPath = Path.Combine(
            resultsPath, $"experiment-{experiment}", "diagnostics", "dotnet-counters", "dotnet-counters.json");
        string directPath = Path.Combine(
            resultsPath, $"experiment-{experiment}", "dotnet-counters.json");

        string? counterPath = File.Exists(diagnosticsPath) ? diagnosticsPath
            : File.Exists(directPath) ? directPath : null;

        if (counterPath is null)
        {
            return null;
        }

        return await ParseCounterJsonAsync(counterPath, ct).ConfigureAwait(false);
    }

    private static async Task<ConsoleCounterData?> LoadBaselineCountersAsync(
        string resultsPath, CancellationToken ct)
    {
        string path = Path.Combine(resultsPath, "baseline-counters.json");
        if (!File.Exists(path))
        {
            return null;
        }

        return await ParseCounterJsonAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses a dotnet-counters JSON file into <see cref="ConsoleCounterData"/>.
    /// The JSON has structure: { Runtime: { CpuUsage: { Avg, ... }, WorkingSetMB: { Avg, ... } } }
    /// </summary>
    private static async Task<ConsoleCounterData?> ParseCounterJsonAsync(
        string path, CancellationToken ct)
    {
        try
        {
            string json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            double? cpuAvg = null;
            double? memoryMB = null;

            if (doc.RootElement.TryGetProperty("Runtime", out JsonElement runtime))
            {
                if (runtime.TryGetProperty("CpuUsage", out JsonElement cpu) &&
                    cpu.TryGetProperty("Avg", out JsonElement cpuVal))
                {
                    cpuAvg = cpuVal.GetDouble();
                }

                if (runtime.TryGetProperty("WorkingSetMB", out JsonElement ws) &&
                    ws.TryGetProperty("Avg", out JsonElement wsVal))
                {
                    memoryMB = wsVal.GetDouble();
                }
            }

            return new ConsoleCounterData(cpuAvg, memoryMB);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Run metadata ──────────────────────────────────────────────────────

    private static async Task<RunMetadata?> LoadRunMetadataAsync(
        string resultsPath, CancellationToken ct)
    {
        string path = Path.Combine(resultsPath, "run-metadata.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<RunMetadata>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Scenarios ─────────────────────────────────────────────────────────

    private static async Task<List<ScenarioResult>> LoadScenariosAsync(
        string resultsPath,
        List<(int Number, MetricSet Metrics)> experiments,
        CancellationToken ct)
    {
        var scenarios = new List<ScenarioResult>();

        // Find all baseline-{scenario}.json files (not baseline.json or baseline-counters.json)
        foreach (string baselineFile in Directory.GetFiles(resultsPath, "baseline-*.json"))
        {
            string fileName = Path.GetFileNameWithoutExtension(baselineFile);
            if (fileName.Equals("baseline-counters", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Extract scenario name: "baseline-myScenario" → "myScenario"
            string scenarioName = fileName["baseline-".Length..];

            MetricSet scenarioBaseline;
            try
            {
                scenarioBaseline = await K6SummaryParser.ParseAsync(
                    baselineFile, experiment: 0, run: 0, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            var baselineRow = new ExperimentRow(0, scenarioBaseline);

            // Find matching experiment scenario files
            var experimentRows = new List<ExperimentRow>();
            foreach ((int number, _) in experiments)
            {
                string expScenarioPath = Path.Combine(
                    resultsPath, $"experiment-{number}", $"k6-summary-{scenarioName}.json");

                if (!File.Exists(expScenarioPath))
                {
                    continue;
                }

                try
                {
                    MetricSet expMetrics = await K6SummaryParser.ParseAsync(
                        expScenarioPath, number, run: 0, ct).ConfigureAwait(false);
                    experimentRows.Add(new ExperimentRow(number, expMetrics));
                }
                catch (JsonException)
                {
                    // Skip malformed scenario files
                }
                catch (IOException)
                {
                    // Skip unreadable scenario files
                }
            }

            scenarios.Add(new ScenarioResult(scenarioName, baselineRow, experimentRows));
        }

        return scenarios;
    }
}
