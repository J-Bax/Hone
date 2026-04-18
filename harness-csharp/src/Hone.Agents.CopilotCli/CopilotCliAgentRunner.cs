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
    private const string AgentFileGlob = "*.agent.md";

    /// <inheritdoc />
    public async Task<AgentRunResult> InvokeAsync(AgentInvocation invocation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        using AgentDefinitionOverlay? overlay = !string.IsNullOrEmpty(invocation.WorkingDirectory)
            ? PrepareAgentDefinitions(invocation.WorkingDirectory)
            : null;

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

    internal static AgentDefinitionOverlay? PrepareAgentDefinitions(
        string workingDirectory,
        string? bundledAgentsBaseDirectory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        string? sourceAgentsDirectory = ResolveBundledAgentsDirectory(bundledAgentsBaseDirectory);
        if (string.IsNullOrEmpty(sourceAgentsDirectory))
        {
            return null;
        }

        string targetAgentsDirectory = Path.Combine(workingDirectory, ".github", "agents");
        bool targetAgentsDirectoryExisted = Directory.Exists(targetAgentsDirectory);
        Directory.CreateDirectory(targetAgentsDirectory);

        var createdFiles = new List<string>();
        var backups = new List<(string DestinationPath, string BackupPath)>();

        foreach (string sourceFile in Directory.EnumerateFiles(sourceAgentsDirectory, AgentFileGlob, SearchOption.TopDirectoryOnly))
        {
            string destinationFile = Path.Combine(targetAgentsDirectory, Path.GetFileName(sourceFile));

            if (File.Exists(destinationFile))
            {
                string existingContent = File.ReadAllText(destinationFile, Encoding.UTF8);
                string sourceContent = File.ReadAllText(sourceFile, Encoding.UTF8);
                if (string.Equals(existingContent, sourceContent, StringComparison.Ordinal))
                {
                    continue;
                }

                string backupPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.agent.md");
                File.Copy(destinationFile, backupPath, overwrite: true);
                backups.Add((destinationFile, backupPath));
            }
            else
            {
                createdFiles.Add(destinationFile);
            }

            File.Copy(sourceFile, destinationFile, overwrite: true);
        }

        return new AgentDefinitionOverlay(
            workingDirectory,
            targetAgentsDirectory,
            targetAgentsDirectoryExisted,
            createdFiles,
            backups);
    }

    internal static string? ResolveBundledAgentsDirectory(string? startDirectory = null)
    {
        string baseDirectory = Path.GetFullPath(startDirectory ?? AppContext.BaseDirectory);

        foreach (string currentDirectory in EnumerateSelfAndParents(baseDirectory))
        {
            string bundledAgentsDirectory = Path.Combine(currentDirectory, "agents");
            if (ContainsAgentDefinitions(bundledAgentsDirectory))
            {
                return bundledAgentsDirectory;
            }

            string repoAgentsDirectory = Path.Combine(currentDirectory, ".github", "agents");
            if (ContainsAgentDefinitions(repoAgentsDirectory))
            {
                return repoAgentsDirectory;
            }
        }

        return null;
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

    private static bool ContainsAgentDefinitions(string directory) =>
        Directory.Exists(directory)
        && Directory.EnumerateFiles(directory, AgentFileGlob, SearchOption.TopDirectoryOnly).Any();

    private static IEnumerable<string> EnumerateSelfAndParents(string directory)
    {
        DirectoryInfo? current = new(directory);
        while (current is not null)
        {
            yield return current.FullName;
            current = current.Parent;
        }
    }

    internal sealed class AgentDefinitionOverlay(
        string workingDirectory,
        string targetAgentsDirectory,
        bool targetAgentsDirectoryExisted,
        IReadOnlyList<string> createdFiles,
        IReadOnlyList<(string DestinationPath, string BackupPath)> backups) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            for (int i = backups.Count - 1; i >= 0; i--)
            {
                (string destinationPath, string backupPath) = backups[i];
                File.Copy(backupPath, destinationPath, overwrite: true);
                File.Delete(backupPath);
            }

            foreach (string createdFile in createdFiles)
            {
                if (File.Exists(createdFile))
                {
                    File.Delete(createdFile);
                }
            }

            if (!targetAgentsDirectoryExisted && Directory.Exists(targetAgentsDirectory) && !Directory.EnumerateFileSystemEntries(targetAgentsDirectory).Any())
            {
                Directory.Delete(targetAgentsDirectory);

                string githubDirectory = Path.Combine(workingDirectory, ".github");
                if (Directory.Exists(githubDirectory) && !Directory.EnumerateFileSystemEntries(githubDirectory).Any())
                {
                    Directory.Delete(githubDirectory);
                }
            }

            _disposed = true;
        }
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
