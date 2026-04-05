using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Diagnostics.Collectors;

/// <summary>
/// Base class for PerfView-based collectors, encapsulating the shared
/// Start/Stop lifecycle (CTS pattern, premature-exit check, abort-file stop flow).
/// Subclasses provide collector-specific PerfView arguments and export logic.
/// </summary>
internal abstract class PerfViewCollectorBase : ICollectorPlugin
{
    protected PerfViewCollectorBase(IProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ProcessRunner = processRunner;
    }

    protected IProcessRunner ProcessRunner { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <summary>ETW session names to clean up before/after collection.</summary>
    protected abstract IReadOnlyList<string> EtwSessionNames { get; }

    /// <summary>Builds the PerfView command-line arguments for collection.</summary>
    protected abstract string[] BuildPerfViewArgs(string outputPath, int processId, CollectorSettings settings);

    /// <inheritdoc />
    public abstract Task<CollectorExportResult> ExportAsync(
        IReadOnlyList<string> artifactPaths,
        string outputDir,
        string processName,
        CollectorSettings settings,
        CancellationToken ct = default);

    /// <inheritdoc />
    public async Task<CollectorStartResult> StartAsync(
        int processId,
        string outputDir,
        CollectorSettings settings,
        CancellationToken ct = default)
    {
        // Clean up stale ETW sessions from prior interrupted runs
        await PerfViewHelper.CleanStaleEtwSessionsAsync(
            ProcessRunner, EtwSessionNames, ct).ConfigureAwait(false);

        string? perfViewExe = settings.PerfViewExePath;
        if (string.IsNullOrEmpty(perfViewExe))
        {
            return new CollectorStartResult(Success: false, Error: "PerfViewExePath not specified in settings.");
        }

        if (!File.Exists(perfViewExe))
        {
            return new CollectorStartResult(Success: false, Error: $"PerfView executable not found at '{perfViewExe}'.");
        }

        Directory.CreateDirectory(outputDir);

        string outputPath = Path.Combine(outputDir, $"{Name}.etl.zip");
        PerfViewHelper.CleanStaleFiles(outputDir, Name);

        string[] perfViewArgs = BuildPerfViewArgs(outputPath, processId, settings);

        int totalTimeout = settings.MaxCollectSec + settings.StopTimeoutSec + 60;
#pragma warning disable CA2000 // CTS ownership is transferred to PerfViewHandle/caller
        var collectionCts = new CancellationTokenSource();
#pragma warning restore CA2000

        try
        {
            Task<ProcessResult> collectionTask = ProcessRunner.RunAsync(
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
                pvHandle, EtwSessionNames, ProcessRunner, ct).ConfigureAwait(false);
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
}
