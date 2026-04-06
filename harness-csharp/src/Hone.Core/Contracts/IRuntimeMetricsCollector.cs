namespace Hone.Core.Contracts;

/// <summary>
/// Generic runtime metrics collection abstraction.
/// </summary>
public interface IRuntimeMetricsCollector
{
    /// <summary>
    /// Starts collecting runtime metrics from the specified process.
    /// </summary>
    public Task<MetricsCollectionHandle> StartAsync(
        int processId,
        RuntimeMetricsOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Stops collection and parses the collected metrics.
    /// </summary>
    public Task<RuntimeMetricsResult> StopAndParseAsync(
        MetricsCollectionHandle handle,
        CancellationToken ct = default);
}
