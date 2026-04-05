using System.Diagnostics;
using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Lifecycle.SharedHooks;

/// <summary>
/// Built-in hook that stops a .NET API process.
/// Replaces <c>hooks/dotnet-stop.ps1</c>.
/// </summary>
public sealed class DotnetStopHook : ILifecycleHook
{
    private static readonly TimeSpan WaitForExitTimeout = TimeSpan.FromSeconds(5);

    public Task<HookResult> ExecuteAsync(HookContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stopwatch = Stopwatch.StartNew();

        return Task.FromResult(StopProcesses(context, stopwatch));
    }

    private static HookResult StopProcesses(HookContext context, Stopwatch stopwatch)
    {
        string? projectPath = null;
        if (!string.IsNullOrEmpty(context.Config.Api.ProjectPath))
        {
            projectPath = Path.GetFullPath(
                Path.Combine(context.TargetPath, context.Config.Api.ProjectPath));
        }

        List<Process> candidates = FindTargetProcesses(projectPath);

        if (candidates.Count == 0)
        {
            stopwatch.Stop();
            return new HookResult(
                Success: true,
                Message: "No running target API processes found",
                Duration: stopwatch.Elapsed,
                Artifacts: [],
                BaseUrl: null,
                Process: null);
        }

        List<string> stopped = [];
        List<string> failures = [];

        foreach (Process candidate in candidates)
        {
            try
            {
                if (candidate.HasExited)
                {
                    continue;
                }

                candidate.Kill(entireProcessTree: true);
                _ = candidate.WaitForExit((int)WaitForExitTimeout.TotalMilliseconds);
                stopped.Add($"{candidate.ProcessName} ({candidate.Id})");
            }
            catch (InvalidOperationException ex)
            {
                failures.Add($"PID {candidate.Id}: {ex.Message}");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                failures.Add($"PID {candidate.Id}: {ex.Message}");
            }
            finally
            {
                candidate.Dispose();
            }
        }

        stopwatch.Stop();

        string message;
        bool success;

        if (failures.Count > 0)
        {
            success = false;
            message = $"Failed to stop some target API processes: {string.Join("; ", failures)}";
        }
        else if (stopped.Count > 0)
        {
            success = true;
            message = $"Stopped target API processes: {string.Join(", ", stopped)}";
        }
        else
        {
            success = true;
            message = "No running target API processes found";
        }

        return new HookResult(
            Success: success,
            Message: message,
            Duration: stopwatch.Elapsed,
            Artifacts: [],
            BaseUrl: null,
            Process: null);
    }

    /// <summary>
    /// Discovers processes related to the target API by matching executable paths
    /// against the project's <c>bin/</c> directory.
    /// PS parity with <c>Get-TargetApiProcessCandidate</c>.
    /// </summary>
    internal static List<Process> FindTargetProcesses(string? projectPath)
    {
        if (string.IsNullOrEmpty(projectPath))
        {
            return [];
        }

        string binPath = Path.Combine(projectPath, "bin");
        List<Process> candidates = [];
        HashSet<int> seen = [];

#pragma warning disable CA1031 // Resilience: process enumeration must not throw — access-denied, exited, or platform errors are expected
        try
        {
            foreach (Process proc in Process.GetProcesses())
            {
                try
                {
                    bool matches = false;

                    // PS parity: match by executable path under bin/ directory
                    try
                    {
                        string? exePath = proc.MainModule?.FileName;
                        if (exePath is not null)
                        {
                            string normalizedExePath = Path.GetFullPath(exePath);
                            if (normalizedExePath.StartsWith(binPath, StringComparison.OrdinalIgnoreCase))
                            {
                                matches = true;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Access denied or process exited — skip
                    }

                    if (matches && seen.Add(proc.Id))
                    {
                        candidates.Add(proc);
                    }
                    else
                    {
                        proc.Dispose();
                    }
                }
                catch (Exception)
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception)
        {
            // Process enumeration failed
        }
#pragma warning restore CA1031

        return candidates;
    }
}
