using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hone.Agents.Core;
using Hone.Core.Contracts;

namespace Hone.Agents.CopilotCli;

/// <summary>
/// Invokes the Copilot CLI as an external process.
/// </summary>
public sealed class CopilotCliAgentRunner : IAgentRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(600);

    /// <inheritdoc />
    public async Task<AgentRunResult> InvokeAsync(AgentInvocation invocation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        TimeSpan timeout = invocation.Timeout ?? DefaultTimeout;
        List<string> args = BuildArguments(invocation);

        var startInfo = new ProcessStartInfo
        {
            FileName = "copilot",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!string.IsNullOrEmpty(invocation.WorkingDirectory))
        {
            startInfo.WorkingDirectory = invocation.WorkingDirectory;
        }

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
        {
            return new AgentRunResult(
                Success: false,
                Output: "Failed to start copilot process",
                TimedOut: false,
                ExitCode: -1);
        }

        // Read streams asynchronously to prevent deadlocks from buffer fill
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout expired (not caller cancellation)
            TryKillProcess(process);
            string partialOutput = await ReadPartialOutputAsync(stdoutTask).ConfigureAwait(false);
            return new AgentRunResult(
                Success: false,
                Output: partialOutput,
                TimedOut: true,
                ExitCode: -1);
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled — propagate to let upstream handle cancellation
            TryKillProcess(process);
            throw;
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        // Ensure stderr is fully consumed to avoid orphaned tasks
        string stderr = await stderrTask.ConfigureAwait(false);
        string output = NormalizeOutput(stdout, stderr, process.ExitCode);

        return new AgentRunResult(
            Success: process.ExitCode == 0,
            Output: output,
            TimedOut: false,
            ExitCode: process.ExitCode);
    }

    /// <summary>
    /// Builds the CLI argument list from an <see cref="AgentInvocation"/>.
    /// </summary>
    internal static List<string> BuildArguments(AgentInvocation invocation)
    {
        List<string> args =
        [
            "--agent", invocation.AgentName,
            "--model", invocation.Model ?? ModelDefaults.CopilotCli,
            "-p", invocation.Prompt,
            "-s",
            "--no-auto-update",
            "--no-ask-user",
            "--output-format", "json",
        ];

        if (!string.IsNullOrEmpty(invocation.WorkingDirectory))
        {
            // When running in the target dir, disable custom instructions to avoid interference
            // from the target's .copilot/ config
            args.Add("--no-custom-instructions");
        }

        if (invocation.AdditionalAllowedDirectories is not null)
        {
            foreach (string directory in invocation.AdditionalAllowedDirectories
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                args.Add("--add-dir");
                args.Add(directory);
            }
        }

        return args;
    }

    internal static string NormalizeOutput(string stdout, string stderr, int exitCode)
    {
        if (TryExtractAssistantMessageContent(stdout, out string? assistantContent))
        {
            return assistantContent!;
        }

        if (exitCode != 0 && string.IsNullOrWhiteSpace(stdout) && !string.IsNullOrWhiteSpace(stderr))
        {
            return stderr;
        }

        return stdout;
    }

    internal static bool TryExtractAssistantMessageContent(string output, out string? content)
    {
        ArgumentNullException.ThrowIfNull(output);

        content = null;
        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;

                if (!root.TryGetProperty("type", out JsonElement typeElement)
                    || !string.Equals(typeElement.GetString(), "assistant.message", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!root.TryGetProperty("data", out JsonElement dataElement)
                    || !dataElement.TryGetProperty("content", out JsonElement contentElement)
                    || contentElement.ValueKind is not JsonValueKind.String)
                {
                    continue;
                }

                string? messageContent = contentElement.GetString();
                if (!string.IsNullOrWhiteSpace(messageContent))
                {
                    content = messageContent;
                }
            }
            catch (JsonException)
            {
                // Skip chatter and keep scanning for valid JSONL assistant messages.
                continue;
            }
        }

        return !string.IsNullOrWhiteSpace(content);
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

#pragma warning disable CA1031 // Catch general exception — best-effort partial output retrieval after timeout/cancel
    private static async Task<string> ReadPartialOutputAsync(Task<string> stdoutTask)
    {
        try
        {
            return await stdoutTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return string.Empty;
        }
    }
#pragma warning restore CA1031
}
