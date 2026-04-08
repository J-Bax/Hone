namespace Hone.Core.Config;

/// <summary>
/// Configuration for the diagnostic profiling plugin framework.
/// </summary>
public sealed record DiagnosticsConfig(
    bool Enabled = true,
    string CollectorsPath = "plugins/collectors",
    string AnalyzersPath = "plugins/analyzers",
    string? PerfViewExePath = "tools/PerfView/PerfView.exe",
    string? DiagnosticScenarioPath = null,
    int DiagnosticRuns = 1,
    int K6TimeoutSec = 300,
    IReadOnlyDictionary<string, CollectorSettingsEntry>? CollectorSettings = null,
    IReadOnlyDictionary<string, AnalyzerSettingsEntry>? AnalyzerSettings = null)
{
    /// <summary>
    /// Gets per-collector settings keyed by collector directory name.
    /// </summary>
    public IReadOnlyDictionary<string, CollectorSettingsEntry> CollectorSettings { get; init; } =
        CollectorSettings ?? new Dictionary<string, CollectorSettingsEntry>(StringComparer.Ordinal)
        {
            ["perfview-cpu"] = new(
                Enabled: true,
                MaxCollectSec: 150,
                StopTimeoutSec: 600,
                ExportTimeoutSec: 600,
                BufferSizeMB: 256,
                MaxStacks: 100),
            ["perfview-gc"] = new(
                Enabled: true,
                MaxCollectSec: 150,
                StopTimeoutSec: 600,
                ExportTimeoutSec: 600,
                BufferSizeMB: 256),
            ["dotnet-counters"] = new(Enabled: true),
        };

    /// <summary>
    /// Gets per-analyzer settings keyed by analyzer directory name.
    /// </summary>
    public IReadOnlyDictionary<string, AnalyzerSettingsEntry> AnalyzerSettings { get; init; } =
        AnalyzerSettings ?? new Dictionary<string, AnalyzerSettingsEntry>(StringComparer.Ordinal)
        {
            ["cpu-hotspots"] = new(Enabled: true, Model: "claude-opus-4.6", MaxStacks: 100),
            ["memory-gc"] = new(Enabled: true, Model: "claude-opus-4.6"),
        };
}
