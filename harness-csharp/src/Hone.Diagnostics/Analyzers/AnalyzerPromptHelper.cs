using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Hone.Core.Models;
using Hone.Core.Utilities;

namespace Hone.Diagnostics.Analyzers;

/// <summary>
/// Shared prompt-building and I/O helpers for analyzer plugins.
/// </summary>
internal static partial class AnalyzerPromptHelper
{
    private const string DefaultModel = "claude-opus-4.6";
    private const int DefaultMaxStacks = 100;

    /// <summary>
    /// Formats a performance-metrics section for inclusion in an analyzer prompt.
    /// </summary>
    internal static string FormatMetricsSection(MetricSet? metrics)
    {
        string p95 = metrics?.HttpReqDuration is not null
            ? metrics.HttpReqDuration.P95.ToString("F1", CultureInfo.InvariantCulture)
            : "N/A";

        string rps = metrics?.HttpReqs is not null
            ? Math.Round(metrics.HttpReqs.Rate, 1).ToString("F1", CultureInfo.InvariantCulture)
            : "N/A";

        string errorRate = metrics?.HttpReqFailed is not null
            ? Math.Round(metrics.HttpReqFailed.Rate * 100, 2).ToString("F2", CultureInfo.InvariantCulture)
            : "N/A";

        return $"""
            ## Current Performance
            - p95 Latency: {p95}ms
            - Requests/sec: {rps}
            - Error rate: {errorRate}%
            """;
    }

    /// <summary>
    /// Resolves a named extra property from a <see cref="CollectorExportResult"/>,
    /// falling back to the exported path at <paramref name="fallbackIndex"/>.
    /// </summary>
    internal static string? ResolveDataPath(
        CollectorExportResult export,
        string propertyName,
        int fallbackIndex = 0)
    {
        if (export.ExtraProperties is not null
            && export.ExtraProperties.TryGetValue(propertyName, out object? value)
            && value is string path
            && !string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (export.ExportedPaths.Count > fallbackIndex)
        {
            return export.ExportedPaths[fallbackIndex];
        }

        return null;
    }

    /// <summary>
    /// Reads the entire contents of a file, or returns <c>null</c> when the file
    /// does not exist.
    /// </summary>
    internal static async Task<string?> ReadFileOrNullAsync(
        string? path,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Truncates folded-stack content to the top <paramref name="maxStacks"/>
    /// entries sorted by trailing sample count descending.
    /// </summary>
    internal static string TruncateStacks(string stacksContent, int maxStacks)
    {
        string[] lines = stacksContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var scored = new List<(string Line, long Count)>();
        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');
            Match m = TrailingCountPattern().Match(line);
            if (m.Success && long.TryParse(m.Groups["count"].ValueSpan, CultureInfo.InvariantCulture, out long count))
            {
                scored.Add((line, count));
            }
        }

        scored.Sort(static (a, b) => b.Count.CompareTo(a.Count));

        var sb = new StringBuilder();
        int limit = Math.Min(maxStacks, scored.Count);
        for (int i = 0; i < limit; i++)
        {
            if (i > 0)
            {
                sb.AppendLine();
            }

            sb.Append(scored[i].Line);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Saves a prompt string to the output directory and returns the file path.
    /// </summary>
    internal static async Task<string> SavePromptAsync(
        string outputDir,
        string fileName,
        string prompt,
        CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        string path = Path.Combine(outputDir, fileName);
        await File.WriteAllTextAsync(path, prompt, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// Saves an agent response to the output directory and returns the file path.
    /// </summary>
    internal static async Task<string> SaveResponseAsync(
        string outputDir,
        string fileName,
        string response,
        CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        string path = Path.Combine(outputDir, fileName);
        await File.WriteAllTextAsync(path, response, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// Extracts the first JSON block from an agent response and parses it as a
    /// <see cref="JsonElement"/>.  Returns <c>null</c> when parsing fails.
    /// </summary>
    internal static JsonElement? ParseJsonReport(string agentOutput)
    {
        string extracted = JsonUtils.ExtractJsonBlock(agentOutput);
        string sanitized = JsonUtils.SanitizeNaN(extracted);

        try
        {
            using var doc = JsonDocument.Parse(sanitized);
            // Clone the root element so it outlives the document.
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the "summary" property from a parsed JSON report element.
    /// </summary>
    internal static string? ExtractSummary(JsonElement? report)
    {
        if (report is null)
        {
            return null;
        }

        if (report.Value.ValueKind == JsonValueKind.Object
            && report.Value.TryGetProperty("summary", out JsonElement summaryEl)
            && summaryEl.ValueKind == JsonValueKind.String)
        {
            return summaryEl.GetString();
        }

        return null;
    }

    /// <summary>
    /// Retrieves the model setting, defaulting to <c>claude-opus-4.6</c>.
    /// </summary>
    internal static string GetModel(IReadOnlyDictionary<string, object?> settings) =>
        GetStringSetting(settings, "Model", DefaultModel);

    /// <summary>
    /// Retrieves the MaxStacks setting, defaulting to 100.
    /// </summary>
    internal static int GetMaxStacks(IReadOnlyDictionary<string, object?> settings) =>
        GetIntSetting(settings, "MaxStacks", DefaultMaxStacks);

    private static string GetStringSetting(
        IReadOnlyDictionary<string, object?> settings,
        string key,
        string defaultValue)
    {
        if (settings.TryGetValue(key, out object? value) && value is string s && !string.IsNullOrEmpty(s))
        {
            return s;
        }

        return defaultValue;
    }

    private static int GetIntSetting(
        IReadOnlyDictionary<string, object?> settings,
        string key,
        int defaultValue)
    {
        if (!settings.TryGetValue(key, out object? value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string s when int.TryParse(s, CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => defaultValue,
        };
    }

    [GeneratedRegex(@"\s+(?<count>\d+)\s*$", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex TrailingCountPattern();
}
