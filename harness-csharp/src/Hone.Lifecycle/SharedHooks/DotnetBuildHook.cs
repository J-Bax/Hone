using System.Diagnostics;
using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Lifecycle.SharedHooks;

/// <summary>
/// Built-in hook that builds a .NET solution.
/// Replaces <c>hooks/dotnet-build.ps1</c>.
/// </summary>
public sealed class DotnetBuildHook(IProcessRunner processRunner) : ILifecycleHook
{
    public async Task<HookResult> ExecuteAsync(HookContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stopwatch = Stopwatch.StartNew();

        string solutionPath = Path.Combine(context.TargetPath, context.Config.Api.SolutionPath);
        ProcessResult result = await processRunner.RunAsync(
            executable: "dotnet",
            arguments: ["build", solutionPath, "--configuration", "Release"],
            workingDirectory: context.TargetPath,
            ct: ct).ConfigureAwait(false);

        stopwatch.Stop();

        List<string> artifacts = [];

        // PS parity: save build output when running under an experiment
        if (context.Experiment > 0)
        {
            string logDir = Path.Combine(context.TargetPath, context.Config.Api.ResultsPath,
                $"experiment-{context.Experiment}");
            Directory.CreateDirectory(logDir);
            string buildLogPath = Path.Combine(logDir, "build.log");
            await File.WriteAllTextAsync(buildLogPath, result.Output, ct).ConfigureAwait(false);
            artifacts.Add(buildLogPath);
        }

        return new HookResult(
            Success: result.Success,
            Message: result.Success ? "Build succeeded" : $"Build failed (exit code {result.ExitCode})",
            Duration: stopwatch.Elapsed,
            Artifacts: artifacts,
            BaseUrl: null,
            Process: null);
    }
}
