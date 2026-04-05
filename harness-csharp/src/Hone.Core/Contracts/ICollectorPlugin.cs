using Hone.Core.Models;

namespace Hone.Core.Contracts;

/// <summary>
/// Contract for diagnostic data collector plugins.
/// Mirrors the PowerShell collector lifecycle: Start → Stop → Export.
/// </summary>
public interface ICollectorPlugin
{
    /// <summary>
    /// Gets the display name of this collector.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Starts data collection for the given process.
    /// </summary>
    public Task<CollectorStartResult> StartAsync(
        int processId,
        string outputDir,
        CollectorSettings settings,
        CancellationToken ct = default);

    /// <summary>
    /// Stops a running collection session and returns artifacts.
    /// </summary>
    public Task<CollectorArtifacts> StopAsync(
        object handle,
        CancellationToken ct = default);

    /// <summary>
    /// Exports collected artifacts into analysis-ready output.
    /// </summary>
    public Task<CollectorExportResult> ExportAsync(
        IReadOnlyList<string> artifactPaths,
        string outputDir,
        string processName,
        CollectorSettings settings,
        CancellationToken ct = default);
}
