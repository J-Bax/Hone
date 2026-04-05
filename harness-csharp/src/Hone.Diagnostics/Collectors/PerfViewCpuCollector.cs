using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Diagnostics.Collectors;

/// <summary>
/// Collector plugin for PerfView CPU sampling.
/// Captures CPU sampling stacks and sampled allocation events.
/// </summary>
internal sealed class PerfViewCpuCollector(IProcessRunner processRunner)
    : PerfViewCollectorBase(processRunner)
{

    /// <inheritdoc />
    public override string Name => "perfview-cpu";

    /// <inheritdoc />
    protected override IReadOnlyList<string> EtwSessionNames =>
        ["NT Kernel Logger", "PerfViewSession"];

    // CPU sampling via kernel Profile events (no /ThreadTime)
    // /ClrEvents:Default includes GC, JIT, Exception for managed stack resolution
    // NO /GCOnly — that would suppress kernel CPU sampling events
    // /DotNetAllocSampled enables sampled allocation tick events (~100KB intervals)
    // /StackCompression:false works around ETW merge bug 0x1069
    /// <inheritdoc />
    protected override string[] BuildPerfViewArgs(string outputPath, int processId, CollectorSettings settings)
    {
        return
        [
            "collect",
            $"/DataFile:{outputPath}",
            "/NoGui",
            "/AcceptEULA",
            $"/MaxCollectSec:{settings.MaxCollectSec}",
            $"/BufferSizeMB:{settings.BufferSizeMB}",
            "/Merge:true",
            "/Zip:true",
            "/NoNGenPdbs",
            "/StackCompression:false",
            "/ClrEvents:Default",
            "/DotNetAllocSampled",
            $"/focusProcess:{processId}",
        ];
    }

    /// <inheritdoc />
    public override async Task<CollectorExportResult> ExportAsync(
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

        string? perfViewExe = settings.PerfViewExePath;
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
            ProcessRunner, perfViewExe, args, timeoutSec, ct).ConfigureAwait(false);

        if (!File.Exists(csvPath))
        {
            return [];
        }

        string csvContent = await PerfViewHelper.ReadFileWithSharingAsync(csvPath, ct).ConfigureAwait(false);
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
                ProcessRunner, perfViewExe, args, timeoutSec, ct).ConfigureAwait(false);

            // PerfView writes to the temp directory when processing zipped ETL files
            if (!File.Exists(allocCsvPath))
            {
                string allocBaseName = PerfViewHelper.GetEtlBaseName(etlPath);
                allocCsvPath = PerfViewHelper.FindInPerfViewTempDir(allocBaseName, ".heapAlloc.csv")
                    ?? allocCsvPath;
            }

            if (File.Exists(allocCsvPath))
            {
                string csvContent = await PerfViewHelper.ReadFileWithSharingAsync(allocCsvPath, ct).ConfigureAwait(false);
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
}
