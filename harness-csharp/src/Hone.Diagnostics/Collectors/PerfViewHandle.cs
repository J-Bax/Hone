using Hone.Core.Models;

namespace Hone.Diagnostics.Collectors;

/// <summary>
/// Holds per-collection state between Start and Stop for PerfView collectors.
/// </summary>
internal sealed class PerfViewHandle(
    Task<ProcessResult> collectionTask,
    CancellationTokenSource collectionCts,
    string outputPath,
    int targetProcessId,
    CollectorSettings settings) : IDisposable
{
    /// <summary>Background task running the PerfView collect process.</summary>
    public Task<ProcessResult> CollectionTask { get; } = collectionTask;

    /// <summary>Token source to cancel (kill) the PerfView process.</summary>
    public CancellationTokenSource CollectionCts { get; } = collectionCts;

    /// <summary>Path to the ETL zip artifact.</summary>
    public string OutputPath { get; } = outputPath;

    /// <summary>PID of the target process being profiled.</summary>
    public int TargetProcessId { get; } = targetProcessId;

    /// <summary>Merged collector settings.</summary>
    public CollectorSettings Settings { get; } = settings;

    /// <inheritdoc />
    public void Dispose()
    {
        CollectionCts.Dispose();
    }
}
