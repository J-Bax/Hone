using System.Diagnostics;
using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Lifecycle.SharedHooks;

/// <summary>
/// Built-in hook that runs k6 scale tests against the running API.
/// </summary>
public sealed class K6RunHook(ILoadTestRunner loadTestRunner) : ILifecycleHook
{
    public async Task<HookResult> ExecuteAsync(HookContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stopwatch = Stopwatch.StartNew();

        if (context.BaseUrl is null)
        {
            stopwatch.Stop();
            return new HookResult(
                Success: false,
                Message: "k6 scale tests require a BaseUrl",
                Duration: stopwatch.Elapsed,
                Artifacts: [],
                BaseUrl: null);
        }

        string scenarioPath = Path.Combine(context.TargetPath, context.Config.ScaleTest.ScenarioPath);
        string outputDir = Path.Combine(context.TargetPath, context.Config.Api.ResultsPath,
            $"experiment-{context.Experiment}");
        Directory.CreateDirectory(outputDir);

        try
        {
            var options = new LoadTestOptions(
                ScenarioPath: scenarioPath,
                BaseUrl: context.BaseUrl,
                OutputDir: outputDir,
                Experiment: context.Experiment,
                Run: 1,
                Timeout: null);

            LoadTestResult result = await loadTestRunner.RunAsync(options, ct).ConfigureAwait(false);

            stopwatch.Stop();

            List<string> artifacts = [];
            if (result.SummaryPath is not null)
            {
                artifacts.Add(result.SummaryPath);
            }

            return new HookResult(
                Success: result.Success,
                Message: result.Success ? "k6 scale tests completed" : "k6 scale tests failed",
                Duration: stopwatch.Elapsed,
                Artifacts: artifacts,
                BaseUrl: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new HookResult(
                Success: false,
                Message: $"k6 scale tests error: {ex.Message}",
                Duration: stopwatch.Elapsed,
                Artifacts: [],
                BaseUrl: null);
        }
    }
}
