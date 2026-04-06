namespace Hone.Core.Config;

/// <summary>
/// Per-analyzer settings for diagnostic profiling.
/// Keys match the directory name under <see cref="DiagnosticsConfig.AnalyzersPath"/>.
/// </summary>
public sealed record AnalyzerSettingsEntry(
    bool Enabled = true,
    string? Model = null,
    int? MaxStacks = null);
