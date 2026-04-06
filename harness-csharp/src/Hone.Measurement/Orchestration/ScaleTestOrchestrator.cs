using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Measurement.Orchestration;

/// <summary>
/// Orchestrates multi-run load testing: warmup → N measured runs → median selection.
/// Uses <see cref="ILoadTestRunner"/> — doesn't know about k6 specifics.
/// </summary>
public sealed class ScaleTestOrchestrator
{
    /// <summary>
    /// Runs the full scale test cycle.
    /// </summary>
    /// <param name="config">Scale test configuration.</param>
    /// <param name="runner">Load test runner.</param>
    /// <param name="baseUrl">Base URL of the API under test.</param>
    /// <param name="outputDir">Output directory for artifacts.</param>
    /// <param name="experiment">Experiment number (0 for baseline).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="afterRunCallback">Optional callback invoked after each k6 run (e.g., trigger server-side GC).</param>
    public static async Task<ScaleTestResult> RunAsync(
        ScaleTestConfig config,
        ILoadTestRunner runner,
        Uri baseUrl,
        string outputDir,
        int experiment,
        Func<CancellationToken, Task>? afterRunCallback = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(runner);

        // 1. Warmup
        if (config.WarmupEnabled && !string.IsNullOrEmpty(config.WarmupScenarioPath))
        {
            var warmupOptions = new LoadTestOptions(
                ScenarioPath: config.WarmupScenarioPath,
                BaseUrl: baseUrl,
                OutputDir: outputDir,
                Experiment: experiment,
                Run: 0,
                Timeout: null);

            _ = await runner.RunAsync(warmupOptions, ct).ConfigureAwait(false);

            if (afterRunCallback is not null)
            {
                await afterRunCallback(ct).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromSeconds(config.CooldownSeconds), ct).ConfigureAwait(false);
        }

        // 2. Measured runs
        List<MetricSet> allRunMetrics = [];

        for (int run = 1; run <= config.MeasuredRuns; run++)
        {
            if (run > 1)
            {
                if (afterRunCallback is not null)
                {
                    await afterRunCallback(ct).ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromSeconds(config.CooldownSeconds), ct).ConfigureAwait(false);
            }

            var options = new LoadTestOptions(
                ScenarioPath: config.ScenarioPath,
                BaseUrl: baseUrl,
                OutputDir: outputDir,
                Experiment: experiment,
                Run: run,
                Timeout: null);

            LoadTestResult result = await runner.RunAsync(options, ct).ConfigureAwait(false);

            if (result.Metrics is not null)
            {
                allRunMetrics.Add(result.Metrics);
            }
        }

        // 3. Median selection
        MetricSet? selectedMetrics = SelectMedian(allRunMetrics);

        // 4. Result
        return new ScaleTestResult(
            Success: selectedMetrics is not null,
            Metrics: selectedMetrics,
            SummaryPath: selectedMetrics?.SummaryPath,
            RunCount: allRunMetrics.Count,
            RunMetrics: allRunMetrics);
    }

    private static MetricSet? SelectMedian(List<MetricSet> metrics)
    {
        if (metrics.Count == 0)
        {
            return null;
        }

        if (metrics.Count == 1)
        {
            return metrics[0];
        }

        // Sort by p95 and pick the median (middle) run.
        // Integer division floors to select the median element.
        List<MetricSet> sorted = [.. metrics.OrderBy(m => m.HttpReqDuration.P95)];
        int medianIndex = sorted.Count / 2;
        return sorted[medianIndex];
    }
}
