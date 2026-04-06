using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Diagnostics.Collection;
using Hone.Diagnostics.Discovery;

namespace Hone.Diagnostics.Measurement;

/// <summary>
/// Orchestrates the full diagnostic profiling lifecycle: multi-pass collection
/// (per group) followed by analyzer execution on merged data.
/// </summary>
/// <remarks>
/// This orchestrator does NOT manage lifecycle hooks (API start/stop, database reset).
/// Those are managed by the loop host. This class handles:
/// <list type="bullet">
///   <item>Collection passes (start collectors → stop → export)</item>
///   <item>Analyzer execution with required-collector validation</item>
///   <item>Failure isolation between passes and between analyzers</item>
/// </list>
/// </remarks>
public sealed class DiagnosticMeasurementOrchestrator
{
    private readonly DiagnosticCollectionOrchestrator _collectionOrchestrator;
    private readonly IReadOnlyDictionary<string, IAnalyzerPlugin> _analyzerPlugins;
    private readonly IHoneEventSink _eventSink;

    /// <summary>
    /// Initializes a new instance of <see cref="DiagnosticMeasurementOrchestrator"/>.
    /// </summary>
    /// <param name="collectionOrchestrator">Orchestrator for collector start/stop/export lifecycle.</param>
    /// <param name="analyzerPlugins">Mapping of analyzer name to plugin implementation.</param>
    /// <param name="eventSink">Sink for diagnostic progress and status events.</param>
    public DiagnosticMeasurementOrchestrator(
        DiagnosticCollectionOrchestrator collectionOrchestrator,
        IReadOnlyDictionary<string, IAnalyzerPlugin> analyzerPlugins,
        IHoneEventSink eventSink)
    {
        ArgumentNullException.ThrowIfNull(collectionOrchestrator);
        ArgumentNullException.ThrowIfNull(analyzerPlugins);
        ArgumentNullException.ThrowIfNull(eventSink);

        _collectionOrchestrator = collectionOrchestrator;
        _analyzerPlugins = analyzerPlugins;
        _eventSink = eventSink;
    }

    /// <summary>
    /// Runs a single collection pass: start collectors → run workload → stop → export.
    /// This method does NOT manage lifecycle hooks (API start/stop) — the caller handles that.
    /// </summary>
    /// <param name="collectors">All discovered collectors for this pass (group + defaults).</param>
    /// <param name="groupName">Name of the collector group for this pass.</param>
    /// <param name="collectorNames">Subset of collector names to activate in this pass.</param>
    /// <param name="processId">PID of the running API process.</param>
    /// <param name="processName">Process name for export (e.g., "dotnet").</param>
    /// <param name="outputDir">Root output directory for collector artifacts.</param>
    /// <param name="workload">Optional async workload to execute while collectors are recording (e.g., k6 run).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Per-pass result with exported collector data.</returns>
    public async Task<CollectionPassResult> RunCollectionPassAsync(
        IReadOnlyList<DiscoveredCollector> collectors,
        string groupName,
        IReadOnlySet<string> collectorNames,
        int processId,
        string processName,
        string outputDir,
        Func<Task>? workload = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collectors);
        ArgumentNullException.ThrowIfNull(groupName);
        ArgumentNullException.ThrowIfNull(collectorNames);
        ArgumentNullException.ThrowIfNull(outputDir);
        ArgumentNullException.ThrowIfNull(processName);

        EmitStatus($"Starting collectors for group '{groupName}'...");
        EmitProgress(groupName, "pass-starting");

        CollectionStartResult startResult = await _collectionOrchestrator.StartAsync(
            collectors, processId, outputDir, collectorNames, ct).ConfigureAwait(false);

        if (startResult.Handles.Count == 0)
        {
            EmitStatus($"No collectors started for group '{groupName}' — skipping");
            return new CollectionPassResult(
                Success: false,
                CollectorData: new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal));
        }

        CollectionStopResult stopResult;
        try
        {
            EmitProgress(groupName, "collecting");
            if (workload is not null)
            {
                await workload().ConfigureAwait(false);
            }
        }
        finally
        {
            // Always stop collectors, even if an exception occurs
            EmitStatus($"Stopping collectors for group '{groupName}'...");
            stopResult = await _collectionOrchestrator.StopAsync(
                startResult.Handles, collectors, ct).ConfigureAwait(false);

            if (!stopResult.Success)
            {
                EmitWarning("Some collectors failed to stop — continuing with available data");
            }
        }

        // Export collector data
        EmitStatus($"Exporting data for group '{groupName}'...");
        CollectionExportResult exportResult;
        try
        {
            exportResult = await _collectionOrchestrator.ExportAsync(
                stopResult.ArtifactMap, collectors, outputDir, processName, collectorNames, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Catch general exception for export failure tolerance
        catch (Exception ex)
#pragma warning restore CA1031
        {
            EmitWarning($"Export failed for group '{groupName}': {ex.Message} — continuing with available data");
            exportResult = new CollectionExportResult(
                Success: false,
                CollectorData: new Dictionary<string, CollectorExportResult>(StringComparer.Ordinal));
        }

        if (!exportResult.Success)
        {
            EmitWarning("Some exports failed — continuing with available data");
        }

        EmitProgress(groupName, "pass-complete");

        return new CollectionPassResult(
            Success: startResult.Success && stopResult.Success && exportResult.Success,
            CollectorData: exportResult.CollectorData);
    }

    /// <summary>
    /// Runs all enabled analyzers against the merged collector data.
    /// Analyzers whose required collectors are missing are skipped with a warning.
    /// Individual analyzer failures are isolated — other analyzers continue.
    /// </summary>
    /// <param name="analyzers">Discovered analyzers to run.</param>
    /// <param name="mergedCollectorData">Union of collector data from all passes.</param>
    /// <param name="currentMetrics">Current performance metrics for analysis context.</param>
    /// <param name="experiment">Current experiment number.</param>
    /// <param name="outputDir">Root output directory for analyzer results.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Analysis result with per-analyzer reports.</returns>
    public async Task<DiagnosticAnalysisResult> RunAnalyzersAsync(
        IReadOnlyList<DiscoveredAnalyzer> analyzers,
        IReadOnlyDictionary<string, CollectorExportResult> mergedCollectorData,
        MetricSet? currentMetrics,
        int experiment,
        string outputDir,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(analyzers);
        ArgumentNullException.ThrowIfNull(mergedCollectorData);
        ArgumentNullException.ThrowIfNull(outputDir);

        if (analyzers.Count == 0)
        {
            EmitStatus("No enabled analyzers");
            return new DiagnosticAnalysisResult(
                Success: true,
                Reports: new Dictionary<string, AnalyzerResult>(StringComparer.Ordinal));
        }

        EmitStatus("Running analyzers...");

        var reports = new Dictionary<string, AnalyzerResult>(StringComparer.Ordinal);
        bool allSuccess = true;

        foreach (DiscoveredAnalyzer analyzer in analyzers)
        {
            // Check required collectors have data
            IReadOnlyList<string> requiredCollectors = analyzer.Metadata.RequiredCollectors;
            var missing = requiredCollectors
                .Where(c => !mergedCollectorData.ContainsKey(c))
                .ToList();

            if (missing.Count > 0)
            {
                EmitWarning(
                    $"Analyzer '{analyzer.Name}' requires collectors ({string.Join(", ", missing)}) " +
                    "but data is missing — skipping");
                continue;
            }

            // Resolve plugin — a missing plugin is a configuration bug; fail loudly.
            if (!_analyzerPlugins.TryGetValue(analyzer.Name, out IAnalyzerPlugin? plugin))
            {
                throw new InvalidOperationException(
                    $"Analyzer '{analyzer.Name}' has no registered plugin. " +
                    "This is a configuration bug — all discovered analyzers must have a corresponding plugin registration. " +
                    $"Registered plugins: [{string.Join(", ", _analyzerPlugins.Keys)}].");
            }

            string analyzerOutputDir = Path.Combine(outputDir, analyzer.Name);
            Directory.CreateDirectory(analyzerOutputDir);

            var context = new AnalyzerContext(
                CollectorData: mergedCollectorData,
                CurrentMetrics: currentMetrics,
                Experiment: experiment,
                Settings: analyzer.MergedSettings,
                OutputDir: analyzerOutputDir);

            EmitStatus($"Running analyzer: {analyzer.Name}");
            EmitProgress(analyzer.Name, "analyzing");

            try
            {
                AnalyzerResult result = await plugin.AnalyzeAsync(context, ct).ConfigureAwait(false);

                if (result.Success)
                {
                    reports[analyzer.Name] = result;
                    EmitStatus($"  {analyzer.Name}: {result.Summary}");
                }
                else
                {
                    allSuccess = false;
                    EmitWarning($"Analyzer '{analyzer.Name}' failed: {result.Error ?? "unknown error"}");
                }
            }
#pragma warning disable CA1031 // Catch general exception for failure isolation
            catch (Exception ex)
#pragma warning restore CA1031
            {
                allSuccess = false;
                EmitWarning($"Analyzer '{analyzer.Name}' threw an exception: {ex.Message}");
            }
        }

        EmitProgress("analyzers", "complete");

        return new DiagnosticAnalysisResult(Success: allSuccess, Reports: reports);
    }

    private void EmitStatus(string message)
    {
        _eventSink.Emit(new StatusMessage(
            Message: message,
            Level: LogLevel.Info,
            Timestamp: DateTimeOffset.UtcNow));
    }

    private void EmitWarning(string message)
    {
        _eventSink.Emit(new StatusMessage(
            Message: message,
            Level: LogLevel.Warning,
            Timestamp: DateTimeOffset.UtcNow));
    }

    private void EmitProgress(string name, string stage)
    {
        _eventSink.Emit(new DiagnosticProgress(
            CollectorName: name,
            Stage: stage,
            Timestamp: DateTimeOffset.UtcNow));
    }
}
