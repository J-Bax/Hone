using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Diagnostics.Collectors;

/// <summary>
/// Collector plugin for PerfView CPU sampling.
/// Replaces <c>harness/collectors/perfview-cpu/</c>.
/// Captures CPU sampling stacks and sampled allocation events.
/// </summary>
internal sealed class PerfViewCpuCollector : ICollectorPlugin
{
    private static readonly IReadOnlyList<string> EtwSessionNames =
        ["NT Kernel Logger", "PerfViewSession"];

    private readonly IProcessRunner _processRunner;

    public PerfViewCpuCollector(IProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        _processRunner = processRunner;
    }

    /// <inheritdoc />
    public string Name => "perfview-cpu";

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

        string outputPath = Path.Combine(outputDir, "perfview-cpu.etl.zip");
        PerfViewHelper.CleanStaleFiles(outputDir, "perfview-cpu");

        int maxCollectSec = settings.MaxCollectSec;
        int bufferSizeMB = settings.BufferSizeMB;

        // CPU sampling via kernel Profile events (no /ThreadTime)
        // /ClrEvents:Default includes GC, JIT, Exception for managed stack resolution
        // NO /GCOnly — that would suppress kernel CPU sampling events
        // /DotNetAllocSampled enables sampled allocation tick events (~100KB intervals)
        // /StackCompression:false works around ETW merge bug 0x1069
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
            "/StackCompression:false",
            "/ClrEvents:Default",
            "/DotNetAllocSampled",
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

        int maxStacks = settings.MaxStacks;
        int exportTimeoutSec = settings.ExportTimeoutSec;
        string cpuStacksPath = Path.Combine(outputDir, "cpu-stacks-folded.txt");
        bool cpuExportSuccess = false;
        string summaryText;
        var exportedPaths = new List<string> { cpuStacksPath };
        bool usedFallback = false;

        try
        {
            // Attempt 1: Export with process name filter
            IReadOnlyList<string> foldedLines = await ExportCpuStacksAsync(
                perfViewExe, etlPath, outputDir, processName,
                filterExcludedModules: false, exportTimeoutSec, ct).ConfigureAwait(false);

            // Attempt 2: Retry without process filter if no stacks found
            if (foldedLines.Count == 0)
            {
                foldedLines = await ExportCpuStacksAsync(
                    perfViewExe, etlPath, outputDir, processFilter: null,
                    filterExcludedModules: true, exportTimeoutSec, ct).ConfigureAwait(false);
                usedFallback = foldedLines.Count > 0;
            }

            if (foldedLines.Count > 0)
            {
                cpuExportSuccess = true;
            }

            List<string> sortedLines = [.. foldedLines.Take(maxStacks)];

            if (sortedLines.Count > 0)
            {
                await File.WriteAllLinesAsync(cpuStacksPath, sortedLines, ct).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllTextAsync(cpuStacksPath,
                    $"[PerfView CPU export did not produce stacks — raw ETL available at {etlPath}] 1",
                    ct).ConfigureAwait(false);
            }

            string fallbackNote = usedFallback ? " (fallback: unfiltered export)" : "";
            summaryText = $"CPU: {sortedLines.Count} stacks exported{fallbackNote}";
        }
#pragma warning disable CA1031 // Catch general exception for export robustness
        catch (Exception ex)
#pragma warning restore CA1031
        {
            summaryText = $"CPU export failed: {ex.Message}";
            await File.WriteAllTextAsync(cpuStacksPath,
                $"[CPU export error: {ex.Message}] 1", ct).ConfigureAwait(false);
        }

        // Export allocation type stacks (from /DotNetAllocSampled)
        try
        {
            string allocTypesPath = Path.Combine(outputDir, "alloc-types-folded.txt");
            bool allocSuccess = await ExportAllocTypesAsync(
                perfViewExe, etlPath, outputDir, processName, allocTypesPath,
                maxStacks, exportTimeoutSec, ct).ConfigureAwait(false);

            if (allocSuccess)
            {
                exportedPaths.Add(allocTypesPath);
                int allocCount = (await File.ReadAllLinesAsync(allocTypesPath, ct)
                    .ConfigureAwait(false)).Length;
                summaryText += $" | Alloc: {allocCount} types exported";
            }
        }
#pragma warning disable CA1031 // Catch general exception — alloc export is non-fatal
        catch (Exception)
#pragma warning restore CA1031
        {
            // Allocation type export failure is non-fatal
        }

        return new CollectorExportResult(
            cpuExportSuccess,
            exportedPaths,
            Summary: summaryText);
    }

    private async Task<IReadOnlyList<string>> ExportCpuStacksAsync(
        string perfViewExe,
        string etlPath,
        string outputDir,
        string? processFilter,
        bool filterExcludedModules,
        int timeoutSec,
        CancellationToken ct)
    {
        string logPath = Path.Combine(outputDir,
            processFilter is not null ? "perfview-cpu-export.log" : "perfview-cpu-export-fallback.log");

        var args = new List<string>
        {
            $"/LogFile:{logPath}",
            "/AcceptEULA",
            "/NoGui",
            "UserCommand",
            "SaveCPUStacksAsCsv",
            etlPath,
        };

        if (!string.IsNullOrEmpty(processFilter))
        {
            args.Add(processFilter);
        }

        // Remove stale CSV before export
        string csvPath = GetExpectedCsvPath(etlPath, ".perfView.csv");
        if (File.Exists(csvPath))
        {
            File.Delete(csvPath);
        }

        _ = await PerfViewHelper.RunPerfViewCommandAsync(
            _processRunner, perfViewExe, args, timeoutSec, ct).ConfigureAwait(false);

        if (!File.Exists(csvPath))
        {
            return [];
        }

        string csvContent = await ReadFileWithSharingAsync(csvPath, ct).ConfigureAwait(false);
        return FoldedStackParser.Parse(csvContent, filterExcludedModules, int.MaxValue);
    }

    private async Task<bool> ExportAllocTypesAsync(
        string perfViewExe,
        string etlPath,
        string outputDir,
        string processName,
        string allocTypesPath,
        int maxStacks,
        int timeoutSec,
        CancellationToken ct)
    {
        // Try with process filter first, then unfiltered fallback
        foreach (string? filterName in new[] { processName, null })
        {
            string logPath = Path.Combine(outputDir,
                filterName is not null ? "perfview-alloc-export.log" : "perfview-alloc-export-fallback.log");

            var args = new List<string>
            {
                $"/LogFile:{logPath}",
                "/AcceptEULA",
                "/NoGui",
                "UserCommand",
                "SaveManagedHeapAllocStacksAsCsv",
                etlPath,
            };

            if (!string.IsNullOrEmpty(filterName))
            {
                args.Add(filterName);
            }

            string allocCsvPath = GetExpectedCsvPath(etlPath, ".heapAlloc.csv");
            if (File.Exists(allocCsvPath))
            {
                File.Delete(allocCsvPath);
            }

            _ = await PerfViewHelper.RunPerfViewCommandAsync(
                _processRunner, perfViewExe, args, timeoutSec, ct).ConfigureAwait(false);

            // PerfView writes to the temp directory when processing zipped ETL files
            if (!File.Exists(allocCsvPath))
            {
                string allocBaseName = PerfViewHelper.GetEtlBaseName(etlPath);
                allocCsvPath = PerfViewHelper.FindInPerfViewTempDir(allocBaseName, ".heapAlloc.csv")
                    ?? allocCsvPath;
            }

            if (File.Exists(allocCsvPath))
            {
                string csvContent = await ReadFileWithSharingAsync(allocCsvPath, ct).ConfigureAwait(false);
                IReadOnlyList<string> lines = FoldedStackParser.Parse(csvContent, filterExcludedModules: false, maxStacks);
                if (lines.Count > 0)
                {
                    await File.WriteAllLinesAsync(allocTypesPath, lines, ct).ConfigureAwait(false);
                    return true;
                }
            }
        }

        return false;
    }

    private static string GetExpectedCsvPath(string etlPath, string suffix)
    {
        string baseName = PerfViewHelper.GetEtlBaseName(etlPath);
        string etlDir = Path.GetDirectoryName(etlPath)!;
        return Path.Combine(etlDir, baseName + suffix);
    }

    private static async Task<string> ReadFileWithSharingAsync(string path, CancellationToken ct)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }
}
