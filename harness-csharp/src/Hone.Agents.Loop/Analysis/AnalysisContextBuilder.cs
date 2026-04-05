using System.Globalization;
using System.Text;
using System.Text.Json;

using Hone.Core.Config;
using Hone.Core.Models;

namespace Hone.Agents.Loop.Analysis;

/// <summary>
/// Builds the <see cref="AnalysisContext"/> consumed by the analysis agent prompt.
/// Pure-function design — all helpers are static.
/// </summary>
public static class AnalysisContextBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Assembles a complete <see cref="AnalysisContext"/> from config, counters,
    /// history files, and diagnostic reports.
    /// </summary>
    public static AnalysisContext Build(
        string targetDir,
        HoneConfig config,
        CounterSummary? counters,
        string? previousRcaExplanation,
        IReadOnlyDictionary<string, AnalyzerReport>? diagnosticReports)
    {
        ArgumentNullException.ThrowIfNull(config);

        IReadOnlyList<string> sourcePaths = CollectSourceFilePaths(targetDir, config.Api);
        string counterCtx = BuildCounterContext(counters);
        string trafficCtx = BuildTrafficContext(targetDir, config.ScaleTest);
        string historyCtx = BuildHistoryContext(targetDir, config.Api, previousRcaExplanation);
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
                // Return paths relative to targetDir, matching PS behavior
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

    internal static string BuildTrafficContext(string targetDir, ScaleTestConfig scaleTest)
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

#pragma warning disable RS0030 // Sync I/O — matches PS behavior; async is a future concern
        string content = File.ReadAllText(scenarioFullPath);
#pragma warning restore RS0030

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

    internal static string BuildHistoryContext(string targetDir, ApiConfig api, string? previousRcaExplanation)
    {
        var sb = new StringBuilder();
        string metadataDir = Path.Combine(targetDir, api.MetadataPath);

        // Experiment log
        string logPath = Path.Combine(metadataDir, "experiment-log.md");
        if (File.Exists(logPath))
        {
#pragma warning disable RS0030 // Sync I/O — matches PS behavior; async is a future concern
            string logContent = File.ReadAllText(logPath);
#pragma warning restore RS0030
            sb.Append("\n## Previously Tried Optimizations\n")
              .Append(logContent)
              .Append('\n');
        }

        // Queue: prefer structured JSON, fall back to markdown
        string queueJsonPath = Path.Combine(metadataDir, "experiment-queue.json");
        string queueMdPath = Path.Combine(metadataDir, "experiment-queue.md");

        if (File.Exists(queueJsonPath))
        {
            AppendQueueFromJson(sb, queueJsonPath);
        }
        else if (File.Exists(queueMdPath))
        {
#pragma warning disable RS0030 // Sync I/O — matches PS behavior; async is a future concern
            string queueContent = File.ReadAllText(queueMdPath);
#pragma warning restore RS0030
            sb.Append("\n## Known Optimization Queue\n")
              .Append(queueContent)
              .Append('\n');
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
            AppendExperimentHistory(sb, runMetadataPath);
        }

        return sb.ToString();
    }

    private static void AppendQueueFromJson(StringBuilder sb, string queueJsonPath)
    {
        try
        {
#pragma warning disable RS0030 // Sync I/O — matches PS behavior; async is a future concern
            string json = File.ReadAllText(queueJsonPath);
#pragma warning restore RS0030
            OptimizationQueue? queue = JsonSerializer.Deserialize<OptimizationQueue>(json, JsonOptions);
            if (queue is null)
            {
                return;
            }

            List<QueueItem> doneItems = [.. queue.Items.Where(i => i.Status == QueueItemStatus.Done)];
            List<QueueItem> pendingItems = [.. queue.Items.Where(i => i.Status == QueueItemStatus.Pending)];

            if (doneItems.Count == 0 && pendingItems.Count == 0)
            {
                return;
            }

            sb.Append("\n## Known Optimization Queue\n");

            foreach (QueueItem item in doneItems)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- [TRIED] `{item.FilePath}` — {item.Explanation} *(experiment {item.TriedByExperiment} — {item.Outcome})*\n");
            }

            foreach (QueueItem item in pendingItems)
            {
                string scopeTag = item.Scope == OpportunityScope.Architecture ? "[ARCHITECTURE] " : "";
                sb.Append(CultureInfo.InvariantCulture, $"- [PENDING] {scopeTag}`{item.FilePath}` — {item.Explanation}\n");
            }
        }
        catch (JsonException)
        {
            // Non-fatal: queue is supplementary context
        }
    }

    private static void AppendExperimentHistory(StringBuilder sb, string runMetadataPath)
    {
        try
        {
#pragma warning disable RS0030 // Sync I/O — matches PS behavior; async is a future concern
            string json = File.ReadAllText(runMetadataPath);
#pragma warning restore RS0030
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
        catch (JsonException)
        {
            // Non-fatal: run-metadata is supplementary context
        }
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
