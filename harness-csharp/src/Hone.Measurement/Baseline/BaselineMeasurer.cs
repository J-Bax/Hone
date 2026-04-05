using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Measurement.Orchestration;

namespace Hone.Measurement.Baseline;

/// <summary>
/// Measures a performance baseline by running scale tests and optionally collecting runtime counter metrics.
/// Replaces the measurement logic from <c>Get-PerformanceBaseline.ps1</c>.
/// </summary>
public sealed class BaselineMeasurer
{
    /// <summary>
    /// Runs baseline measurement: scale tests via <see cref="ScaleTestOrchestrator"/> plus optional counter collection.
    /// </summary>
    /// <param name="scaleConfig">Scale test configuration (scenario, runs, warmup, etc.).</param>
    /// <param name="countersConfig">Optional counter collection configuration. Pass <c>null</c> to skip counters.</param>
    /// <param name="runner">Load test runner abstraction.</param>
    /// <param name="collector">Optional runtime metrics collector. Pass <c>null</c> to skip counters.</param>
    /// <param name="baseUrl">Base URL of the running API under test.</param>
    /// <param name="outputDir">Directory for test output artifacts.</param>
    /// <param name="processId">Process ID of the API to collect counters from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Baseline result containing metrics, optional counter data, and scale test detail.</returns>
    public static async Task<BaselineResult> MeasureAsync(
        ScaleTestConfig scaleConfig,
        DotnetCountersConfig? countersConfig,
        ILoadTestRunner runner,
        IRuntimeMetricsCollector? collector,
        Uri baseUrl,
        string outputDir,
        int processId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scaleConfig);
        ArgumentNullException.ThrowIfNull(runner);

        // 1. Start counter collection if configured and available
        MetricsCollectionHandle? counterHandle = null;
        IRuntimeMetricsCollector? activeCollector = null;

        if (countersConfig is { Enabled: true } && collector is not null)
        {
            activeCollector = collector;

            var metricsOptions = new RuntimeMetricsOptions(
                Providers: countersConfig.Providers,
                RefreshIntervalSeconds: countersConfig.RefreshIntervalSeconds);

            counterHandle = await activeCollector.StartAsync(processId, metricsOptions, ct).ConfigureAwait(false);
        }

        // 2. Run scale tests (experiment 0 for baselines — PS parity)
        ScaleTestResult scaleResult = await ScaleTestOrchestrator.RunAsync(
            scaleConfig, runner, baseUrl, outputDir, experiment: 0, ct).ConfigureAwait(false);

        // 3. Stop counter collection if started
        IReadOnlyDictionary<string, double>? counterMetrics = null;

        if (activeCollector is not null && counterHandle is not null)
        {
            RuntimeMetricsResult counterResult = await activeCollector.StopAndParseAsync(counterHandle, ct).ConfigureAwait(false);

            if (counterResult.Success)
            {
                counterMetrics = counterResult.Counters;
            }
        }

        // 4. Assemble result — Success = metrics present (threshold failures OK for baselines)
        return new BaselineResult(
            Success: scaleResult.Metrics is not null,
            Metrics: scaleResult.Metrics,
            CounterMetrics: counterMetrics,
            ScaleTestDetail: scaleResult);
    }
}
