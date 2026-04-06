using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Lifecycle.SharedHooks;

/// <summary>
/// Built-in hook that runs .NET E2E tests as the regression gate.
/// </summary>
public sealed partial class DotnetTestHook(IProcessRunner processRunner) : ILifecycleHook
{
    [GeneratedRegex(@"Total tests:\s*(?<count>\d+)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TotalTestsPattern();

    [GeneratedRegex(@"Passed:\s*(?<count>\d+)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PassedTestsPattern();

    [GeneratedRegex(@"Failed:\s*(?<count>\d+)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FailedTestsPattern();

    public async Task<HookResult> ExecuteAsync(HookContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stopwatch = Stopwatch.StartNew();

        string testProjectPath = Path.Combine(context.TargetPath, context.Config.Api.TestProjectPath);
        string resultsDir = Path.Combine(context.TargetPath, context.Config.Api.ResultsPath,
            $"experiment-{context.Experiment}");
        Directory.CreateDirectory(resultsDir);

        string trxPath = Path.Combine(resultsDir, "e2e-results.trx");

        ProcessResult result = await processRunner.RunAsync(
            executable: "dotnet",
            arguments: ["test", testProjectPath, "--configuration", "Release",
                "--logger", $"trx;LogFileName={trxPath}", "--verbosity", "normal",],
            workingDirectory: context.TargetPath,
            ct: ct).ConfigureAwait(false);

        stopwatch.Stop();

        // Parse test counts from output
        int totalTests = ParseCount(TotalTestsPattern(), result.Output);
        int passedTests = ParseCount(PassedTestsPattern(), result.Output);
        int failedTests = ParseCount(FailedTestsPattern(), result.Output);

        // Save test output log
        string testLogPath = Path.Combine(resultsDir, "e2e-tests.log");
        await File.WriteAllTextAsync(testLogPath, result.Output, ct).ConfigureAwait(false);

        return new HookResult(
            Success: result.Success,
            Message: result.Success
                ? $"{passedTests}/{totalTests} tests passed"
                : $"{failedTests}/{totalTests} tests FAILED",
            Duration: stopwatch.Elapsed,
            Artifacts: [trxPath, testLogPath],
            BaseUrl: null);
    }

    private static int ParseCount(Regex pattern, string output)
    {
        Match match = pattern.Match(output);
        return match.Success ? int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture) : 0;
    }
}
