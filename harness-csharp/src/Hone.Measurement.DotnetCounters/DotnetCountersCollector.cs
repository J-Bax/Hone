using Hone.Core.Contracts;

namespace Hone.Measurement.DotnetCounters;

/// <summary>
/// <see cref="IRuntimeMetricsCollector"/> implementation using <c>dotnet-counters</c>.
/// </summary>
/// <remarks>
/// Phase 2 boundary: <see cref="StartAsync"/> prepares the collection handle (output path
/// and process ID) but does <b>not</b> start the <c>dotnet-counters</c> process itself.
/// Background process lifecycle management will be added in the orchestration layer (Phase 7+).
/// <see cref="StopAndParseAsync"/> reads and parses the CSV that was written by the
/// externally-managed <c>dotnet-counters</c> process.
/// </remarks>
/// <param name="processRunner">
/// Process runner for executing <c>dotnet-counters</c>. Reserved for Phase 7+
/// when background process management is implemented.
/// </param>
public sealed class DotnetCountersCollector(IProcessRunner processRunner) : IRuntimeMetricsCollector
{
    // Will be used in Phase 7+ for background process management.
#pragma warning disable IDE0052, CA1823 // Reserved for Phase 7+ orchestration layer
    private readonly IProcessRunner _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
#pragma warning restore IDE0052, CA1823

    /// <inheritdoc />
    public Task<MetricsCollectionHandle> StartAsync(
        int processId,
        RuntimeMetricsOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), processId, "Process ID must be positive.");
        }

        string outputDir = Path.Combine(Path.GetTempPath(), "hone", "dotnet-counters");
        Directory.CreateDirectory(outputDir);

        string outputPath = Path.Combine(
            outputDir,
            $"counters_{processId}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.csv");

        var handle = new DotnetCountersHandle(outputPath, processId);
        return Task.FromResult(new MetricsCollectionHandle(handle));
    }

    /// <inheritdoc />
    public async Task<RuntimeMetricsResult> StopAndParseAsync(
        MetricsCollectionHandle handle,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.Handle is not DotnetCountersHandle dcHandle)
        {
            return new RuntimeMetricsResult(Success: false, Counters: null);
        }

        if (!File.Exists(dcHandle.OutputPath))
        {
            return new RuntimeMetricsResult(Success: false, Counters: null);
        }

        string csvContent = await File.ReadAllTextAsync(dcHandle.OutputPath, ct).ConfigureAwait(false);
        CounterParseResult parsed = CounterCsvParser.Parse(csvContent);

        return new RuntimeMetricsResult(Success: true, Counters: parsed.Counters);
    }
}
