using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Measurement.K6;

/// <summary>
/// <see cref="ILoadTestRunner"/> implementation that invokes k6 via <see cref="IProcessRunner"/>.
/// Replaces the k6 invocation logic from PowerShell <c>Invoke-ScaleTests.ps1</c>.
/// </summary>
public sealed class K6LoadTestRunner(IProcessRunner processRunner) : ILoadTestRunner
{
    private readonly IProcessRunner _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));

    /// <inheritdoc />
    public async Task<LoadTestResult> RunAsync(LoadTestOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        Directory.CreateDirectory(options.OutputDir);

        string summaryPath = Path.Combine(options.OutputDir, $"k6-summary-run{options.Run}.json");

        List<string> arguments = BuildArguments(options, summaryPath);

        ProcessResult processResult = await _processRunner.RunAsync(
            executable: "k6",
            arguments: arguments,
            workingDirectory: null,
            timeout: options.Timeout,
            ct: ct).ConfigureAwait(false);

        if (processResult.TimedOut)
        {
            return new LoadTestResult(
                Success: false,
                Metrics: null,
                SummaryPath: null,
                Output: "k6 process timed out");
        }

        if (!File.Exists(summaryPath))
        {
            return new LoadTestResult(
                Success: false,
                Metrics: null,
                SummaryPath: null,
                Output: processResult.Output);
        }

        MetricSet metrics = await K6SummaryParser.ParseAsync(
            summaryPath, options.Experiment, options.Run, ct).ConfigureAwait(false);

        return new LoadTestResult(
            Success: true,
            Metrics: metrics,
            SummaryPath: summaryPath,
            Output: processResult.Output);
    }

    private static List<string> BuildArguments(LoadTestOptions options, string summaryPath)
    {
        var args = new List<string>
        {
            "run",
            "--env", $"BASE_URL={options.BaseUrl}",
            "--summary-export", summaryPath,
        };

        if (options.EnvironmentVars is not null)
        {
            foreach (KeyValuePair<string, string> kvp in options.EnvironmentVars)
            {
                args.Add("--env");
                args.Add($"{kvp.Key}={kvp.Value}");
            }
        }

        args.Add(options.ScenarioPath);

        return args;
    }
}
