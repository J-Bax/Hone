using Hone.Core.Models;

namespace Hone.Diagnostics.Collectors;

/// <summary>
/// Holds per-collection state between Start and Stop for dotnet-counters.
/// </summary>
internal sealed class DotnetCountersHandle(
    Task<ProcessResult> collectionTask,
    CancellationTokenSource collectionCts,
    string outputPath) : IDisposable
{
    /// <summary>Background task running the dotnet-counters process.</summary>
    public Task<ProcessResult> CollectionTask { get; } = collectionTask;

    /// <summary>Token source to cancel (stop) the dotnet-counters process.</summary>
    public CancellationTokenSource CollectionCts { get; } = collectionCts;

    /// <summary>Path to the CSV output file.</summary>
    public string OutputPath { get; } = outputPath;

    /// <inheritdoc />
    public void Dispose()
    {
        CollectionCts.Dispose();
    }
}
