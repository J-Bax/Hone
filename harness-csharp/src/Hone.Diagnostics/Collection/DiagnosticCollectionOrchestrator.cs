using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Diagnostics.Discovery;

namespace Hone.Diagnostics.Collection;

/// <summary>
/// Manages the lifecycle of diagnostic collectors: discovering groups,
/// starting, stopping, and exporting collector data.
/// Replaces harness/Invoke-DiagnosticCollection.ps1.
/// </summary>
public sealed class DiagnosticCollectionOrchestrator
{
    private readonly IReadOnlyDictionary<string, ICollectorPlugin> _plugins;
    private readonly IHoneEventSink _eventSink;

    /// <summary>
    /// Initializes a new instance of <see cref="DiagnosticCollectionOrchestrator"/>.
    /// </summary>
    /// <param name="plugins">Mapping of collector name to plugin implementation.</param>
    /// <param name="eventSink">Sink for diagnostic progress events.</param>
    public DiagnosticCollectionOrchestrator(
        IReadOnlyDictionary<string, ICollectorPlugin> plugins,
        IHoneEventSink eventSink)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(eventSink);

        _plugins = plugins;
        _eventSink = eventSink;
    }

    /// <summary>
    /// Groups collectors for multi-pass scheduling.
    /// Default-group collectors are included in every non-default group.
    /// If only default collectors exist, returns a single "default" group.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance method for API consistency")]
    public IReadOnlyDictionary<string, IReadOnlyList<DiscoveredCollector>> GetGroups(
        IReadOnlyList<DiscoveredCollector> collectors)
    {
        ArgumentNullException.ThrowIfNull(collectors);

        var defaultCollectors = collectors
            .Where(c => string.Equals(c.Group, "default", StringComparison.Ordinal))
            .ToList();

        var nonDefaultCollectors = collectors
            .Where(c => !string.Equals(c.Group, "default", StringComparison.Ordinal))
            .ToList();

        var groups = new Dictionary<string, IReadOnlyList<DiscoveredCollector>>(StringComparer.Ordinal);

        if (nonDefaultCollectors.Count == 0)
        {
            groups["default"] = defaultCollectors;
        }
        else
        {
            foreach (IGrouping<string, DiscoveredCollector> grouping in
                nonDefaultCollectors.GroupBy(c => c.Group, StringComparer.Ordinal))
            {
                groups[grouping.Key] = [.. grouping, .. defaultCollectors];
            }
        }

        return groups;
    }

    /// <summary>
    /// Starts all collectors (or a subset), creating per-collector output directories.
    /// Individual collector failures are isolated — other collectors continue.
    /// </summary>
    public async Task<CollectionStartResult> StartAsync(
        IReadOnlyList<DiscoveredCollector> collectors,
        int processId,
        string outputDir,
        IReadOnlySet<string>? collectorSubset = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectors);
        ArgumentNullException.ThrowIfNull(outputDir);

        IReadOnlyList<DiscoveredCollector> filtered = ApplySubsetFilter(collectors, collectorSubset);

        var handles = new Dictionary<string, object>(StringComparer.Ordinal);
        bool allSuccess = true;

        foreach (DiscoveredCollector collector in filtered)
        {
            if (!_plugins.TryGetValue(collector.Name, out ICollectorPlugin? plugin))
            {
                EmitProgress(collector.Name, "skipped-no-plugin");
                allSuccess = false;
                continue;
            }

            string collectorOutputDir = Path.Combine(outputDir, collector.Name);
            Directory.CreateDirectory(collectorOutputDir);

            EmitProgress(collector.Name, "starting");

            try
            {
                CollectorStartResult result = await plugin.StartAsync(
                    processId, collectorOutputDir, collector.MergedSettings, ct).ConfigureAwait(false);

                if (result.Success && result.Handle is not null)
                {
                    handles[collector.Name] = result.Handle;
                }
                else
                {
                    allSuccess = false;
                    EmitProgress(collector.Name, "start-failed");
                }
            }
#pragma warning disable CA1031 // Catch general exception for failure isolation
            catch (Exception)
#pragma warning restore CA1031
            {
                allSuccess = false;
                EmitProgress(collector.Name, "start-error");
            }
        }

        return new CollectionStartResult(allSuccess, handles);
    }

    /// <summary>
    /// Stops collectors that have handles. Collectors without handles are skipped.
    /// Individual collector failures are isolated.
    /// </summary>
    public async Task<CollectionStopResult> StopAsync(
        IReadOnlyDictionary<string, object> handles,
        IReadOnlyList<DiscoveredCollector> collectors,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(collectors);

        var artifactMap = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        bool allSuccess = true;

        foreach (DiscoveredCollector collector in collectors)
        {
            if (!handles.ContainsKey(collector.Name))
            {
                continue;
            }

            if (!_plugins.TryGetValue(collector.Name, out ICollectorPlugin? plugin))
            {
                allSuccess = false;
                continue;
            }

            EmitProgress(collector.Name, "stopping");

            try
            {
                CollectorArtifacts result = await plugin.StopAsync(
                    handles[collector.Name], ct).ConfigureAwait(false);

                if (result.Success)
                {
                    artifactMap[collector.Name] = result.ArtifactPaths;
                }
                else
                {
                    allSuccess = false;
                    EmitProgress(collector.Name, "stop-failed");
                }
            }
#pragma warning disable CA1031 // Catch general exception for failure isolation
            catch (Exception)
#pragma warning restore CA1031
            {
                allSuccess = false;
                EmitProgress(collector.Name, "stop-error");
            }
        }

        return new CollectionStopResult(allSuccess, artifactMap);
    }

    /// <summary>
    /// Exports collector data for collectors that have artifacts.
    /// Extra properties from the export result are preserved.
    /// Individual collector failures are isolated.
    /// </summary>
    public async Task<CollectionExportResult> ExportAsync(
        IReadOnlyDictionary<string, IReadOnlyList<string>> artifactMap,
        IReadOnlyList<DiscoveredCollector> collectors,
        string outputDir,
        string processName,
        IReadOnlySet<string>? collectorSubset = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifactMap);
        ArgumentNullException.ThrowIfNull(collectors);
        ArgumentNullException.ThrowIfNull(outputDir);
        ArgumentNullException.ThrowIfNull(processName);

        IReadOnlyList<DiscoveredCollector> filtered = ApplySubsetFilter(collectors, collectorSubset);

        var collectorData = new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal);
        bool allSuccess = true;

        foreach (DiscoveredCollector collector in filtered)
        {
            if (!artifactMap.TryGetValue(collector.Name, out IReadOnlyList<string>? artifacts))
            {
                continue;
            }

            if (!_plugins.TryGetValue(collector.Name, out ICollectorPlugin? plugin))
            {
                allSuccess = false;
                continue;
            }

            string exportDir = Path.Combine(outputDir, collector.Name);
            Directory.CreateDirectory(exportDir);

            EmitProgress(collector.Name, "exporting");

            try
            {
                CollectorExportResult result = await plugin.ExportAsync(
                    artifacts, exportDir, processName, collector.MergedSettings, ct).ConfigureAwait(false);

                if (result.Success)
                {
                    collectorData[collector.Name] = result;
                }
                else
                {
                    allSuccess = false;
                    EmitProgress(collector.Name, "export-failed");
                }
            }
#pragma warning disable CA1031 // Catch general exception for failure isolation
            catch (Exception)
#pragma warning restore CA1031
            {
                allSuccess = false;
                EmitProgress(collector.Name, "export-error");
            }
        }

        return new CollectionExportResult(allSuccess, collectorData);
    }

    private static IReadOnlyList<DiscoveredCollector> ApplySubsetFilter(
        IReadOnlyList<DiscoveredCollector> collectors,
        IReadOnlySet<string>? subset)
    {
        if (subset is null || subset.Count == 0)
        {
            return collectors;
        }

        return [.. collectors.Where(c => subset.Contains(c.Name))];
    }

    private void EmitProgress(string collectorName, string stage)
    {
        _eventSink.Emit(new DiagnosticProgress(
            CollectorName: collectorName,
            Stage: stage,
            Timestamp: DateTimeOffset.UtcNow));
    }
}
