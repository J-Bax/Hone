using System.Text.Json;

using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Diagnostics.Collectors;

/// <summary>
/// Collector plugin for PerfView GC-only collection.
/// Replaces <c>harness/collectors/perfview-gc/</c>.
/// Captures GC events and produces a structured GC statistics report.
/// </summary>
internal sealed class PerfViewGcCollector : ICollectorPlugin
{
    private static readonly IReadOnlyList<string> EtwSessionNames =
        ["NT Kernel Logger", "PerfViewGCSession"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly IProcessRunner _processRunner;

    public PerfViewGcCollector(IProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        _processRunner = processRunner;
    }

    /// <inheritdoc />
    public string Name => "perfview-gc";

    /// <inheritdoc />
    public async Task<CollectorStartResult> StartAsync(
        int processId,
        string outputDir,
        CollectorSettings settings,
        CancellationToken ct = default)
    {
        // Clean up stale ETW sessions from prior interrupted runs
        await PerfViewHelper.CleanStaleEtwSessionsAsync(
            _processRunner, EtwSessionNames, ct).ConfigureAwait(false);

        string? perfViewExe = PerfViewHelper.ResolvePerfViewExePath(settings);
        if (string.IsNullOrEmpty(perfViewExe))
        {
            return new CollectorStartResult(Success: false, Error: "PerfViewExePath not specified in settings.");
        }

        if (!File.Exists(perfViewExe))
        {
            return new CollectorStartResult(Success: false, Error: $"PerfView executable not found at '{perfViewExe}'.");
        }

        Directory.CreateDirectory(outputDir);

        string outputPath = Path.Combine(outputDir, "perfview-gc.etl.zip");
        PerfViewHelper.CleanStaleFiles(outputDir, "perfview-gc");

        int maxCollectSec = settings.MaxCollectSec;
        int bufferSizeMB = settings.BufferSizeMB;

        // /GCOnly: minimal overhead, captures only GC-related events
        // /ClrEvents:GC ensures GC events are enabled (redundant with /GCOnly but explicit)
        // /focusProcess scopes merge/analysis to the target process
        string[] perfViewArgs =
        [
            "collect",
            $"/DataFile:{outputPath}",
            "/NoGui",
            "/AcceptEULA",
            $"/MaxCollectSec:{maxCollectSec}",
            $"/BufferSizeMB:{bufferSizeMB}",
            "/Merge:true",
            "/Zip:true",
            "/NoNGenPdbs",
            "/GCOnly",
            "/ClrEvents:GC",
            $"/focusProcess:{processId}",
        ];

        int totalTimeout = maxCollectSec + settings.StopTimeoutSec + 60;
#pragma warning disable CA2000 // CTS ownership is transferred to PerfViewHandle/caller
        var collectionCts = new CancellationTokenSource();
#pragma warning restore CA2000

        try
        {
            Task<ProcessResult> collectionTask = _processRunner.RunAsync(
                perfViewExe,
                perfViewArgs,
                timeout: TimeSpan.FromSeconds(totalTimeout),
                ct: collectionCts.Token);

            // Wait briefly to let PerfView initialize
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);

            if (collectionTask.IsCompleted)
            {
                collectionCts.Dispose();
                ProcessResult result = collectionTask.IsCompletedSuccessfully
                    ? await collectionTask.ConfigureAwait(false)
                    : new ProcessResult(Success: false, Output: "", ExitCode: -1, TimedOut: false);
                return new CollectorStartResult(Success: false,
                    Error: $"PerfView exited prematurely with exit code {result.ExitCode}.");
            }

#pragma warning disable CA2000 // Handle ownership is transferred to caller via CollectorStartResult.Handle
            var handle = new PerfViewHandle(
                collectionTask, collectionCts, outputPath, processId, settings);
#pragma warning restore CA2000
            return new CollectorStartResult(Success: true, Handle: handle);
        }
        catch
        {
            await collectionCts.CancelAsync().ConfigureAwait(false);
            collectionCts.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<CollectorArtifacts> StopAsync(
        object handle,
        CancellationToken ct = default)
    {
        if (handle is not PerfViewHandle pvHandle)
        {
            return new CollectorArtifacts(Success: false, ArtifactPaths: []);
        }

        try
        {
            return await PerfViewHelper.StopCollectionAsync(
                pvHandle, EtwSessionNames, _processRunner, ct).ConfigureAwait(false);
        }
        finally
        {
            // If the caller's token was cancelled during StopAsync, PerfView may
            // still be running. Force-cancel the collection CTS and wait briefly
            // so we don't orphan the process with active ETW sessions.
            if (!pvHandle.CollectionTask.IsCompleted)
            {
                try
                {
                    await pvHandle.CollectionCts.CancelAsync().ConfigureAwait(false);
                    _ = await pvHandle.CollectionTask.WaitAsync(
                        TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when we cancel
                }
                catch (TimeoutException)
                {
                    // Process didn't exit in time
                }
#pragma warning disable CA1031 // Best-effort cleanup
                catch (Exception)
#pragma warning restore CA1031
                {
                    // Swallow — we've done our best to stop the process
                }
            }

            pvHandle.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<CollectorExportResult> ExportAsync(
        IReadOnlyList<string> artifactPaths,
        string outputDir,
        string processName,
        CollectorSettings settings,
        CancellationToken ct = default)
    {
        string? etlPath = artifactPaths.Count > 0 ? artifactPaths[0] : null;
        if (string.IsNullOrEmpty(etlPath) || !File.Exists(etlPath))
        {
            return new CollectorExportResult(Success: false, ExportedPaths: [], Summary: $"ETL artifact not found: {etlPath}");
        }

        string? perfViewExe = PerfViewHelper.ResolvePerfViewExePath(settings);
        if (string.IsNullOrEmpty(perfViewExe) || !File.Exists(perfViewExe))
        {
            return new CollectorExportResult(Success: false, ExportedPaths: [],
                Summary: $"PerfView executable not found: {perfViewExe}");
        }

        Directory.CreateDirectory(outputDir);

        string gcReportPath = Path.Combine(outputDir, "gc-report.json");
        int exportTimeoutSec = settings.ExportTimeoutSec;
        string summaryText;

        try
        {
            // Run PerfView GCStats command
            string gcLogPath = Path.Combine(outputDir, "perfview-gcstats.log");
            string[] gcStatsArgs =
            [
                $"/LogFile:{gcLogPath}",
                "/AcceptEULA",
                "/NoGui",
                "UserCommand",
                "GCStats",
                etlPath,
            ];

            _ = await PerfViewHelper.RunPerfViewCommandAsync(
                _processRunner, perfViewExe, gcStatsArgs, exportTimeoutSec, ct).ConfigureAwait(false);

            // Locate the GCStats HTML — PerfView writes alongside the ETL
            string? gcStatsHtml = FindGcStatsHtml(etlPath);

            GcReport report;
            if (gcStatsHtml is not null)
            {
                string htmlContent = await ReadFileWithSharingAsync(gcStatsHtml, ct).ConfigureAwait(false);
                report = GcReportParser.Parse(htmlContent, processName);
            }
            else
            {
                report = new GcReport();
            }

            string json = JsonSerializer.Serialize(report, JsonOptions);
            await File.WriteAllTextAsync(gcReportPath, json, ct).ConfigureAwait(false);

            int gen0Count = report.GenerationStats.Gen0.Count;
            int gen1Count = report.GenerationStats.Gen1.Count;
            int gen2Count = report.GenerationStats.Gen2.Count;
            double gcPauseRatio = report.PauseStats.GcPauseRatio;
            double peakHeapMB = report.HeapStats.PeakSizeMB;

            summaryText = $"GC: Gen0={gen0Count} Gen1={gen1Count} Gen2={gen2Count} " +
                          $"| Pause ratio={gcPauseRatio}% | Heap peak: {peakHeapMB}MB";
        }
#pragma warning disable CA1031 // Catch general exception for export robustness
        catch (Exception ex)
#pragma warning restore CA1031
        {
            summaryText = $"GC export failed: {ex.Message}";

            // Write default report so analyzer gets something
            var defaultReport = new GcReport();
            string json = JsonSerializer.Serialize(defaultReport, JsonOptions);
            await File.WriteAllTextAsync(gcReportPath, json, ct).ConfigureAwait(false);
        }

        return new CollectorExportResult(
            File.Exists(gcReportPath),
            [gcReportPath],
            Summary: summaryText);
    }

    /// <summary>
    /// Searches for the GCStats HTML file in expected locations.
    /// </summary>
    private static string? FindGcStatsHtml(string etlPath)
    {
        string? etlDir = Path.GetDirectoryName(etlPath);
        string baseName = PerfViewHelper.GetEtlBaseName(etlPath);

        if (etlDir is null)
        {
            return null;
        }

        // Check expected locations
        string[] candidates =
        [
            Path.Combine(etlDir, $"{baseName}.gcStats.html"),
            Path.Combine(etlDir, $"{baseName}.GCStats.html"),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Fallback: PerfView temp directory (used when processing zipped ETL files)
        string? tempResult = PerfViewHelper.FindInPerfViewTempDir(baseName, ".gcStats.html");
        if (tempResult is not null)
        {
            return tempResult;
        }

        // Fallback: search the ETL directory for any GCStats HTML
        try
        {
            string? found = Directory.EnumerateFiles(etlDir, "*GCStats*html", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (found is not null)
            {
                return found;
            }
        }
        catch (IOException)
        {
            // Ignore search errors
        }

        return null;
    }

    private static async Task<string> ReadFileWithSharingAsync(string path, CancellationToken ct)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }
}
