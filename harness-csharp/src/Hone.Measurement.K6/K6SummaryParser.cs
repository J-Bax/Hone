using System.Globalization;
using System.Text.Json;
using Hone.Core.Models;

namespace Hone.Measurement.K6;

/// <summary>
/// Parses k6 JSON summary output into a <see cref="MetricSet"/>.
/// Replaces PowerShell <c>Convert-HoneK6SummaryToMetricSet</c>.
/// </summary>
public sealed class K6SummaryParser
{
    /// <summary>
    /// Parses a k6 JSON summary file into a <see cref="MetricSet"/>.
    /// </summary>
    public static async Task<MetricSet> ParseAsync(
        string jsonPath,
        int experiment,
        int run,
        CancellationToken cancellationToken = default)
    {
        string json = await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false);
        return ParseContent(json, experiment, run, jsonPath);
    }

    /// <summary>
    /// Parses k6 JSON summary content (already loaded) into a <see cref="MetricSet"/>.
    /// </summary>
    public static MetricSet ParseContent(string json, int experiment, int run, string? summaryPath = null)
    {
        using var doc = JsonDocument.Parse(json);
        JsonElement metrics = doc.RootElement.GetProperty("metrics");

        JsonElement duration = metrics.GetProperty("http_req_duration");
        var httpReqDuration = new HttpReqDurationMetrics(
            Avg: duration.GetProperty("avg").GetDouble(),
            P50: duration.GetProperty("med").GetDouble(),
            P90: duration.GetProperty("p(90)").GetDouble(),
            P95: duration.GetProperty("p(95)").GetDouble(),
            P99: duration.GetProperty("p(99)").GetDouble(),
            Max: duration.GetProperty("max").GetDouble());

        JsonElement reqs = metrics.GetProperty("http_reqs");
        var httpReqs = new HttpReqCountMetrics(
            Count: reqs.GetProperty("count").GetInt64(),
            Rate: reqs.GetProperty("rate").GetDouble());

        JsonElement failed = metrics.GetProperty("http_req_failed");

        long failedCount = 0;
        if (failed.TryGetProperty("passes", out JsonElement passesEl) &&
            passesEl.ValueKind != JsonValueKind.Null)
        {
            failedCount = passesEl.GetInt64();
        }

        double failedRate = 0;
        if (failed.TryGetProperty("value", out JsonElement valueEl) &&
            valueEl.ValueKind != JsonValueKind.Null)
        {
            failedRate = valueEl.GetDouble();
        }

        var httpReqFailed = new HttpReqFailedMetrics(Count: failedCount, Rate: failedRate);

        return new MetricSet(
            Timestamp: DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Experiment: experiment,
            Run: run,
            HttpReqDuration: httpReqDuration,
            HttpReqs: httpReqs,
            HttpReqFailed: httpReqFailed,
            SummaryPath: summaryPath);
    }
}
