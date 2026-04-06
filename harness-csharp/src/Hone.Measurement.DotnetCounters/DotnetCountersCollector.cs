using Hone.Core.Contracts;

namespace Hone.Measurement.DotnetCounters;

/// <summary>
/// <see cref="IRuntimeMetricsCollector"/> implementation using <c>dotnet-counters</c>.
/// </summary>
/// <remarks>
/// <see cref="StartAsync"/> prepares the collection handle (output path
/// and process ID). <see cref="StopAndParseAsync"/> reads and parses the CSV
/// that was written by the <c>dotnet-counters</c> process.
/// </remarks>
/// <param name="processRunner">
/// Process runner for executing <c>dotnet-counters</c>.
/// </param>
public sealed class DotnetCountersCollector(IProcessRunner processRunner) : IRuntimeMetricsCollector
{
#pragma warning disable IDE0052, CA1823 // Field is injected for future background process management
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
