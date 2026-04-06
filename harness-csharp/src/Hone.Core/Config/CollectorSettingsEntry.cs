namespace Hone.Core.Config;

/// <summary>
/// Per-collector settings for diagnostic profiling.
/// Keys match the directory name under <see cref="DiagnosticsConfig.CollectorsPath"/>.
/// </summary>
public sealed record CollectorSettingsEntry(
    bool Enabled = true,
    int? MaxCollectSec = null,
    int? StopTimeoutSec = null,
    int? ExportTimeoutSec = null,
    int? BufferSizeMB = null,
    int? MaxStacks = null);
