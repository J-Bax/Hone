using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Measurement.K6;

/// <summary>
/// <see cref="ILoadTestRunner"/> implementation that invokes k6 via <see cref="IProcessRunner"/>.
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

        // Default to 5-minute timeout to prevent indefinite hangs (e.g. TCP exhaustion on Windows)
        TimeSpan effectiveTimeout = options.Timeout ?? TimeSpan.FromMinutes(5);

        ProcessResult processResult = await _processRunner.RunAsync(
            executable: "k6",
            arguments: arguments,
            workingDirectory: options.WorkingDirectory,
            timeout: effectiveTimeout,
            ct: ct).ConfigureAwait(false);

        // Save k6 stdout/stderr to log file for diagnostics
        string logPath = Path.Combine(options.OutputDir, $"k6-run{options.Run}.log");
        await File.WriteAllTextAsync(logPath, processResult.Output ?? string.Empty, ct).ConfigureAwait(false);

        // Even on timeout, k6 may have written the summary file before hanging.
        // Attempt to recover metrics from the file regardless of exit status.
        if (File.Exists(summaryPath))
        {
            MetricSet metrics = await K6SummaryParser.ParseAsync(
                summaryPath, options.Experiment, options.Run, ct).ConfigureAwait(false);

            return new LoadTestResult(
                Success: !processResult.TimedOut,
                Metrics: metrics,
                SummaryPath: summaryPath,
                Output: processResult.TimedOut ? "k6 process timed out (metrics recovered from summary file)" : processResult.Output);
        }

        return new LoadTestResult(
            Success: false,
            Metrics: null,
            SummaryPath: null,
            Output: processResult.TimedOut ? "k6 process timed out" : processResult.Output);
    }

    private static List<string> BuildArguments(LoadTestOptions options, string summaryPath)
    {
        var args = new List<string>
        {
            "run",
            "--quiet",
            "--env", $"BASE_URL={options.BaseUrl.GetLeftPart(UriPartial.Authority)}",
            "--summary-export", summaryPath,
            "--summary-trend-stats", "avg,min,med,max,p(90),p(95),p(99)",
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
