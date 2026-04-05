using System.Text.Json;

using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Diagnostics.Collectors;

/// <summary>
/// Collector plugin for PerfView GC-only collection.
/// Captures GC events and produces a structured GC statistics report.
/// </summary>
internal sealed class PerfViewGcCollector(IProcessRunner processRunner)
    : PerfViewCollectorBase(processRunner)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <inheritdoc />
    public override string Name => "perfview-gc";

    /// <inheritdoc />
    protected override IReadOnlyList<string> EtwSessionNames =>
        ["NT Kernel Logger", "PerfViewGCSession"];

    // /GCOnly: minimal overhead, captures only GC-related events
    // /ClrEvents:GC ensures GC events are enabled (redundant with /GCOnly but explicit)
    // /focusProcess scopes merge/analysis to the target process
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
            "/GCOnly",
            "/ClrEvents:GC",
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
                ProcessRunner, perfViewExe, gcStatsArgs, exportTimeoutSec, ct).ConfigureAwait(false);

            // Locate the GCStats HTML — PerfView writes alongside the ETL
            string? gcStatsHtml = FindGcStatsHtml(etlPath);

            GcReport report;
            if (gcStatsHtml is not null)
            {
                string htmlContent = await PerfViewHelper.ReadFileWithSharingAsync(gcStatsHtml, ct).ConfigureAwait(false);
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
}
