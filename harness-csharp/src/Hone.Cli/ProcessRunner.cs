using System.Diagnostics;
using System.Text;

using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Cli;

/// <summary>
/// Default <see cref="IProcessRunner"/> implementation that spawns external processes.
/// </summary>
internal sealed class ProcessRunner : IProcessRunner
{
    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (string arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
        {
            return new ProcessResult(
                Success: false,
                Output: $"Failed to start process: {executable}",
                ExitCode: -1,
                TimedOut: false);
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);

        if (timeout.HasValue)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout.Value);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKillProcess(process);
                string partialOutput = await ReadPartialOutputAsync(stdoutTask).ConfigureAwait(false);
                return new ProcessResult(
                    Success: false,
                    Output: partialOutput,
                    ExitCode: -1,
                    TimedOut: true);
            }
        }
        else
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        string output = string.IsNullOrEmpty(stderr)
            ? stdout
            : $"{stdout}\n{stderr}";

        return new ProcessResult(
            Success: process.ExitCode == 0,
            Output: output,
            ExitCode: process.ExitCode,
            TimedOut: false);
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best-effort cleanup
        }
    }

#pragma warning disable CA1031 // Catch general exception — best-effort partial output retrieval after timeout
    private static async Task<string> ReadPartialOutputAsync(Task<string> stdoutTask)
    {
        try
        {
            return await stdoutTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
#pragma warning restore CA1031
}
