namespace Hone.Reporting.Dashboard;

/// <summary>
/// Pre-serialised data payloads for the HTML dashboard template.
/// All JSON strings are injected verbatim into template placeholders.
/// </summary>
public sealed record DashboardData
{
    /// <summary>JSON array of per-experiment k6 summary objects.</summary>
    public required string DataJson { get; init; }

    /// <summary>JSON array of per-experiment dotnet-counters summary objects.</summary>
    public required string CounterJson { get; init; }

    /// <summary>JSON object of per-experiment dotnet-counters time-series data.</summary>
    public required string TimeSeriesJson { get; init; }

    /// <summary>JSON object of run metadata (machine info) or the literal string "null".</summary>
    public required string RunMetadataJson { get; init; }

    /// <summary>JSON object keyed by scenario name, each containing an array of experiment entries.</summary>
    public required string ScenarioJson { get; init; }

    /// <summary>JSON array of per-experiment chart-friendly counter data (CPU avg/max, working set, heap).</summary>
    public required string CounterChartJson { get; init; }

    /// <summary>Minimum improvement percentage threshold (already multiplied by 100).</summary>
    public required double MinImprovePct { get; init; }

    /// <summary>Maximum regression percentage threshold (already multiplied by 100).</summary>
    public required double MaxRegressPct { get; init; }

    /// <summary>
    /// Timestamp shown in the dashboard header.
    /// When <see langword="null"/>, <see cref="DashboardExporter.Build"/> uses <see cref="System.DateTimeOffset.UtcNow"/>.
    /// </summary>
    public DateTimeOffset? GeneratedAtUtc { get; init; }
}
