using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Diagnostics.Collectors;

/// <summary>
/// Shared PerfView operations used by both CPU and GC collectors.
/// </summary>
internal static class PerfViewHelper
{
    /// <summary>
    /// Runs <c>logman stop</c> for each ETW session name to clean up stale sessions
    /// from prior interrupted runs.
    /// </summary>
    public static async Task CleanStaleEtwSessionsAsync(
        IProcessRunner processRunner,
        IReadOnlyList<string> sessionNames,
        CancellationToken ct)
    {
        foreach (string session in sessionNames)
        {
            // Best-effort cleanup — ignore errors if session wasn't running
            _ = await processRunner.RunAsync(
                "logman",
                ["stop", session, "-ets"],
                timeout: TimeSpan.FromSeconds(10),
                ct: ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes stale intermediate files from a prior interrupted PerfView run.
    /// </summary>
    public static void CleanStaleFiles(string outputDir, string baseName)
    {
        string baseFilePath = Path.Combine(outputDir, baseName);
        string[] staleExtensions =
        [
            ".etl", ".kernel.etl", ".clrRundown.etl",
            ".etl.new", ".etl.zip",
            ".etl.log.txt", ".etl.zip.abort",
        ];

        foreach (string ext in staleExtensions)
        {
            string filePath = baseFilePath + ext;
            if (File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                }
                catch (IOException)
                {
                    // Best-effort cleanup
                }
                catch (UnauthorizedAccessException)
                {
                    // Best-effort cleanup — matches PowerShell's -ErrorAction SilentlyContinue
                }
            }
        }
    }

    /// <summary>
    /// Computes the PerfView log path from the ETL zip output path.
    /// PerfView writes its log to <c>&lt;DataFile without .zip&gt;.log.txt</c>.
    /// </summary>
    public static string GetLogPath(string outputPath)
    {
        return outputPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? string.Concat(outputPath.AsSpan(0, outputPath.Length - 4), ".log.txt")
            : outputPath + ".log.txt";
    }

    /// <summary>
    /// Resolves the base name (without double extension) from an ETL path.
    /// <c>perfview-cpu.etl.zip</c> → <c>perfview-cpu</c>.
    /// </summary>
    public static string GetEtlBaseName(string etlPath)
    {
        return Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(etlPath));
    }

    /// <summary>
    /// Full PerfView stop flow: abort file → log polling → /NoGui hang workaround.
    /// </summary>
    public static async Task<CollectorArtifacts> StopCollectionAsync(
        PerfViewHandle handle,
        IReadOnlyList<string> etwSessionNames,
        IProcessRunner processRunner,
        CancellationToken ct)
    {
        int stopTimeoutSec = handle.Settings.StopTimeoutSec;

        if (handle.CollectionTask.IsCompleted)
        {
            // Process already exited — just check for the artifact
            return MakeArtifactResult(handle.OutputPath);
        }

        // PerfView's documented abort mechanism: create a <DataFile>.abort file.
        // Use CancellationToken.None — this trivial I/O must always complete so
        // PerfView sees the abort signal even when the caller's token is cancelled.
        string abortFilePath = handle.OutputPath + ".abort";
        await File.WriteAllTextAsync(abortFilePath, "stop", CancellationToken.None).ConfigureAwait(false);

        string logPath = GetLogPath(handle.OutputPath);

        // Poll for completion: PerfView writes "[DONE" to log when finished,
        // but has a known bug where the /NoGui process doesn't exit afterward.
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(stopTimeoutSec);
        bool workComplete = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (handle.CollectionTask.IsCompleted)
            {
                break;
            }

            if (File.Exists(handle.OutputPath) &&
                await LogContainsDoneMarkerAsync(logPath, ct).ConfigureAwait(false))
            {
                workComplete = true;
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }

        if (handle.CollectionTask.IsCompleted)
        {
            // Process exited on its own
        }
        else if (workComplete)
        {
            // PerfView completed work but didn't exit (known /NoGui bug).
            // Give a short grace period, then terminate.
            await GracefulStopAsync(handle, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        else
        {
            // Deadline reached without completion — force stop
            await ForceStopAsync(handle).ConfigureAwait(false);

            // Force-kill during active work leaves orphaned ETW sessions
            await CleanStaleEtwSessionsAsync(
                processRunner, etwSessionNames, CancellationToken.None).ConfigureAwait(false);
        }

        // Clean up abort file
        TryDeleteFile(abortFilePath);

        // Allow file handles to flush
        await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);

        return MakeArtifactResult(handle.OutputPath);
    }

    /// <summary>
    /// Runs a PerfView UserCommand with the specified timeout.
    /// </summary>
    public static async Task<ProcessResult> RunPerfViewCommandAsync(
        IProcessRunner processRunner,
        string perfViewExe,
        IEnumerable<string> arguments,
        int timeoutSec,
        CancellationToken ct)
    {
        return await processRunner.RunAsync(
            perfViewExe,
            arguments,
            timeout: TimeSpan.FromSeconds(timeoutSec),
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates and resolves the PerfView executable path from settings.
    /// </summary>
    public static string? ResolvePerfViewExePath(CollectorSettings settings)
    {
        string? perfViewExe = settings.PerfViewExePath;
        if (string.IsNullOrEmpty(perfViewExe))
        {
            return null;
        }

        return perfViewExe;
    }

    /// <summary>
    /// Searches the PerfView temp directory for a file matching the given pattern.
    /// PerfView writes output files here when processing zipped ETL files.
    /// </summary>
    public static string? FindInPerfViewTempDir(string baseName, string fileSuffix)
    {
        string perfViewTempDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", "PerfView");

        if (!Directory.Exists(perfViewTempDir))
        {
            return null;
        }

        try
        {
            // Match the PowerShell pattern: Sort-Object LastWriteTime -Descending | Select-Object -First 1
            return Directory.EnumerateFiles(perfViewTempDir, $"{baseName}*{fileSuffix}")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task GracefulStopAsync(PerfViewHandle handle, TimeSpan grace)
    {
        try
        {
            _ = await handle.CollectionTask.WaitAsync(grace, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await ForceStopAsync(handle).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Process was cancelled, which is fine
        }
#pragma warning disable CA1031 // Catch general exception for cleanup robustness
        catch (Exception)
#pragma warning restore CA1031
        {
            // Swallow any process errors during graceful stop
        }
    }

    private static async Task ForceStopAsync(PerfViewHandle handle)
    {
        await handle.CollectionCts.CancelAsync().ConfigureAwait(false);
        try
        {
            _ = await handle.CollectionTask.WaitAsync(
                TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when we cancel
        }
        catch (TimeoutException)
        {
            // Process didn't respond to cancellation — may be orphaned
        }
#pragma warning disable CA1031 // Catch general exception for cleanup robustness
        catch (Exception)
#pragma warning restore CA1031
        {
            // Swallow any process errors during force stop
        }
    }

    private static async Task<bool> LogContainsDoneMarkerAsync(
        string logPath, CancellationToken ct)
    {
        if (!File.Exists(logPath))
        {
            return false;
        }

        try
        {
            // Use FileShare.ReadWrite because PerfView may still be writing
            using var stream = new FileStream(
                logPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string content = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            return content.Contains("[DONE", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static CollectorArtifacts MakeArtifactResult(string outputPath)
    {
        return File.Exists(outputPath)
            ? new CollectorArtifacts(Success: true, ArtifactPaths: [outputPath])
            : new CollectorArtifacts(Success: false, ArtifactPaths: []);
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup
        }
    }
}
