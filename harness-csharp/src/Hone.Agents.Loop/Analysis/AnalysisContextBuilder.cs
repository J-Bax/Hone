using System.Globalization;
using System.Text;
using System.Text.Json;

using Hone.Core.Config;
using Hone.Core.Models;
using Hone.Core.Observability;

namespace Hone.Agents.Loop.Analysis;

/// <summary>
/// Builds the <see cref="AnalysisContext"/> consumed by the analysis agent prompt.
/// Pure-function design — all helpers are static.
/// </summary>
public static class AnalysisContextBuilder
{
    /// <summary>
    /// Maximum characters to retain from experiment-log.md. Older content is truncated
    /// to prevent unbounded context growth in the analysis prompt.
    /// </summary>
    internal const int MaxExperimentLogChars = 4000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Assembles a complete <see cref="AnalysisContext"/> from config, counters,
    /// history files, and diagnostic reports.
    /// </summary>
    public static async Task<AnalysisContext> BuildAsync(
        string targetDir,
        HoneConfig config,
        CounterSummary? counters,
        string? previousRcaExplanation,
        IReadOnlyDictionary<string, AnalyzerReport>? diagnosticReports,
        IHoneEventSink? eventSink = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        IReadOnlyList<string> sourcePaths = CollectSourceFilePaths(targetDir, config.Api);
        string counterCtx = BuildCounterContext(counters);
        string trafficCtx = await BuildTrafficContextAsync(targetDir, config.ScaleTest, ct).ConfigureAwait(false);
        string historyCtx = await BuildHistoryContextAsync(targetDir, config.Api, previousRcaExplanation, config.Loop.MaxHistoryExperiments, eventSink, ct).ConfigureAwait(false);
        string profilingCtx = BuildProfilingContext(diagnosticReports);

        return new AnalysisContext(sourcePaths, counterCtx, trafficCtx, historyCtx, profilingCtx);
    }

    // ── Source file paths ────────────────────────────────────────────────────

    internal static IReadOnlyList<string> CollectSourceFilePaths(string targetDir, ApiConfig api)
    {
        string projectPath = Path.Combine(targetDir, api.ProjectPath);
        string glob = string.IsNullOrEmpty(api.SourceFileGlob) ? "*.*" : api.SourceFileGlob;

        List<string> results = [];

        foreach (string subPath in api.SourceCodePaths)
        {
            string searchDir = Path.Combine(projectPath, subPath);
            if (!Directory.Exists(searchDir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(searchDir, glob, SearchOption.AllDirectories))
            {
                // Return paths relative to targetDir
                string relative = Path.GetRelativePath(targetDir, file);
                results.Add(relative);
            }
        }

        return results;
    }

    // ── Counter metrics context ──────────────────────────────────────────────

    internal static string BuildCounterContext(CounterSummary? counters)
    {
        if (counters is null)
        {
            return string.Empty;
        }

        return $"""

## Runtime Counters
- CPU avg: {counters.CpuAvg}
- GC heap max: {counters.GcHeapMax}
- Gen2 collections: {counters.Gen2Collections}
- Thread pool max threads: {counters.ThreadPoolMaxThreads}
""";
    }

    // ── Traffic distribution context ─────────────────────────────────────────

    internal static async Task<string> BuildTrafficContextAsync(string targetDir, ScaleTestConfig scaleTest, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(scaleTest.ScenarioPath))
        {
            return string.Empty;
        }

        string scenarioFullPath = Path.Combine(targetDir, scaleTest.ScenarioPath);
        if (!File.Exists(scenarioFullPath))
        {
            return string.Empty;
        }

        string content = await File.ReadAllTextAsync(scenarioFullPath, ct).ConfigureAwait(false);

        return $"""

## Traffic Distribution (k6 Scenario)
The following k6 load test scenario defines the request patterns and relative weights of each
endpoint. Use this to estimate what percentage of total traffic each endpoint/code path receives.

```javascript
{content}
```
""";
    }

    // ── Optimization history context ─────────────────────────────────────────

    internal static async Task<string> BuildHistoryContextAsync(
        string targetDir, ApiConfig api, string? previousRcaExplanation,
        int maxHistoryExperiments, IHoneEventSink? eventSink, CancellationToken ct)
    {
        var sb = new StringBuilder();
        string metadataDir = Path.Combine(targetDir, api.MetadataPath);

        // Experiment log (truncated to prevent context bloat)
        string logPath = Path.Combine(metadataDir, "experiment-log.md");
        if (File.Exists(logPath))
        {
            string logContent = await File.ReadAllTextAsync(logPath, ct).ConfigureAwait(false);
            if (logContent.Length > MaxExperimentLogChars)
            {
                logContent = string.Concat(
                    "*(Truncated — showing most recent entries)*\n",
                    logContent.AsSpan(logContent.Length - MaxExperimentLogChars));
            }

            sb.Append("\n## Previously Tried Optimizations\n")
              .Append(logContent)
              .Append('\n');
        }

        // Queue: structured JSON only
        string queueJsonPath = Path.Combine(metadataDir, "experiment-queue.json");
        if (File.Exists(queueJsonPath))
        {
            await AppendQueueFromJsonAsync(sb, queueJsonPath, maxHistoryExperiments, eventSink, ct).ConfigureAwait(false);
        }

        // Previous RCA explanation
        if (!string.IsNullOrEmpty(previousRcaExplanation))
        {
            sb.Append("\n## Last Experiment's Fix\n")
              .Append(previousRcaExplanation)
              .Append('\n');
        }

        // Structured experiment history from run-metadata.json
        string runMetadataPath = Path.Combine(targetDir, api.ResultsPath, "run-metadata.json");
        if (File.Exists(runMetadataPath))
        {
            await AppendExperimentHistoryAsync(sb, runMetadataPath, maxHistoryExperiments, eventSink, ct).ConfigureAwait(false);
        }

        return sb.ToString();
    }

    private static async Task AppendQueueFromJsonAsync(
        StringBuilder sb, string queueJsonPath, int maxDoneItems, IHoneEventSink? eventSink, CancellationToken ct)
    {
        try
        {
            string json = await File.ReadAllTextAsync(queueJsonPath, ct).ConfigureAwait(false);
            OptimizationQueue? queue = JsonSerializer.Deserialize<OptimizationQueue>(json, JsonOptions);
            if (queue is null)
            {
                return;
            }

            List<QueueItem> allDoneItems = [.. queue.Items
                .Where(i => i.Status == QueueItemStatus.Done)
                .OrderBy(i => i.TriedByExperiment ?? 0),];
            List<QueueItem> pendingItems = [.. queue.Items.Where(i => i.Status == QueueItemStatus.Pending)];

            if (allDoneItems.Count == 0 && pendingItems.Count == 0)
            {
                return;
            }

            sb.Append("\n## Known Optimization Queue\n");

            // Cap done items to the most recent N
            int omittedCount = Math.Max(0, allDoneItems.Count - maxDoneItems);
            if (omittedCount > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"*({omittedCount} earlier tried items omitted)*\n");
            }

            List<QueueItem> recentDoneItems = allDoneItems.Count > maxDoneItems
                ? allDoneItems[omittedCount..]
                : allDoneItems;

            foreach (QueueItem item in recentDoneItems)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- [TRIED] `{item.FilePath}` — {item.Explanation} *(experiment {item.TriedByExperiment} — {item.Outcome})*\n");
            }

            foreach (QueueItem item in pendingItems)
            {
                string scopeTag = item.Scope == OpportunityScope.Architecture ? "[ARCHITECTURE] " : "";
                sb.Append(CultureInfo.InvariantCulture, $"- [PENDING] {scopeTag}`{item.FilePath}` — {item.Explanation}\n");
            }
        }
        catch (JsonException ex)
        {
            eventSink?.Emit(new StatusMessage(
                $"Failed to parse optimization queue JSON at '{queueJsonPath}': {ex.Message}",
                LogLevel.Warning,
                DateTimeOffset.UtcNow));
        }
    }

    private static async Task AppendExperimentHistoryAsync(
        StringBuilder sb, string runMetadataPath, int maxDetailedExperiments, IHoneEventSink? eventSink, CancellationToken ct)
    {
        try
        {
            string json = await File.ReadAllTextAsync(runMetadataPath, ct).ConfigureAwait(false);
            RunMetadata? runMeta = JsonSerializer.Deserialize<RunMetadata>(json, JsonOptions);
            if (runMeta is null)
            {
                return;
            }

            List<ExperimentMetadata> experiments = [.. runMeta.Experiments.Where(e => e is not null)];
            if (experiments.Count == 0)
            {
                return;
            }

            sb.Append("\n## Experiment History (with metrics)\n");
            sb.Append("Do NOT re-attempt optimizations that were already tried and resulted in stale or regressed outcomes. Propose different targets or approaches instead.\n");

            // Summarise older experiments beyond the window
            if (experiments.Count > maxDetailedExperiments)
            {
                List<ExperimentMetadata> older = experiments[..^maxDetailedExperiments];
                AppendExperimentSummary(sb, older);
                experiments = experiments[^maxDetailedExperiments..];
            }

            sb.Append("| Exp | File | Outcome | p95 (ms) | RPS | Branch |\n");
            sb.Append("|-----|------|---------|----------|-----|--------|\n");

            foreach (ExperimentMetadata exp in experiments)
            {
                string p95 = exp.P95.HasValue
                    ? exp.P95.Value.ToString("F1", CultureInfo.InvariantCulture)
                    : "N/A";
                string rps = exp.RPS.HasValue
                    ? exp.RPS.Value.ToString("F1", CultureInfo.InvariantCulture)
                    : "N/A";
                string branch = !string.IsNullOrEmpty(exp.BranchName) ? exp.BranchName : "—";

                sb.Append(CultureInfo.InvariantCulture, $"| {exp.Experiment} | — | {exp.Outcome} | {p95} | {rps} | {branch} |\n");
            }
        }
        catch (JsonException ex)
        {
            eventSink?.Emit(new StatusMessage(
                $"Failed to parse run metadata JSON at '{runMetadataPath}': {ex.Message}",
                LogLevel.Warning,
                DateTimeOffset.UtcNow));
        }
    }

    private static void AppendExperimentSummary(StringBuilder sb, List<ExperimentMetadata> older)
    {
        var countsByOutcome = older
            .Where(e => e.Outcome.HasValue)
            .GroupBy(e => e.Outcome!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var parts = new List<string>();
        foreach (ExperimentOutcome outcome in Enum.GetValues<ExperimentOutcome>())
        {
            if (countsByOutcome.TryGetValue(outcome, out int count))
            {
                parts.Add($"{count} {outcome}");
            }
        }

        string outcomeSummary = parts.Count > 0
            ? string.Join(", ", parts)
            : "no outcomes recorded";

        double? bestP95 = older.Where(e => e.P95.HasValue).Select(e => e.P95!.Value).Order().FirstOrDefault();
        double? bestRps = older.Where(e => e.RPS.HasValue).Select(e => e.RPS!.Value).OrderDescending().FirstOrDefault();

        string metricsSummary = (bestP95, bestRps) switch
        {
            (not null, not null) => string.Create(CultureInfo.InvariantCulture, $" — best p95: {bestP95.Value:F1}ms, best RPS: {bestRps.Value:F1}"),
            (not null, null) => string.Create(CultureInfo.InvariantCulture, $" — best p95: {bestP95.Value:F1}ms"),
            (null, not null) => string.Create(CultureInfo.InvariantCulture, $" — best RPS: {bestRps.Value:F1}"),
            _ => string.Empty,
        };

        sb.Append(CultureInfo.InvariantCulture, $"*{older.Count} earlier experiments ({outcomeSummary}){metricsSummary}*\n");
    }

    // ── Diagnostic profiling context ─────────────────────────────────────────

    internal static string BuildProfilingContext(IReadOnlyDictionary<string, AnalyzerReport>? reports)
    {
        if (reports is null || reports.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("\n## Diagnostic Profiling Reports");
        sb.Append("\n(Captured during a separate profiling run — numbers may differ from evaluation due to profiling overhead)\n");

        foreach (string analyzerName in reports.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            AnalyzerReport entry = reports[analyzerName];
            string reportJson = entry.Report ?? entry.Summary ?? string.Empty;

            sb.Append(CultureInfo.InvariantCulture, $"\n### {analyzerName}\n```json\n{reportJson}\n```\n");
        }

        return sb.ToString();
    }
}
