using System.Globalization;

using Hone.Agents.Loop.Implementer;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Orchestration.Implementer;

namespace Hone.Cli;

/// <summary>
/// Wires the real service implementations to the <see cref="IImplementerPipeline"/> contract.
/// Each method delegates to the appropriate component from Phases 1-8.
/// </summary>
internal sealed class ImplementerPipelineAdapter : IImplementerPipeline
{
    private readonly ImplementerAgent _implementerAgent;
    private readonly IVersionControl _versionControl;
    private readonly IProcessRunner _processRunner;
    private readonly IHoneEventSink _eventSink;

    internal ImplementerPipelineAdapter(
        ImplementerAgent implementerAgent,
        IVersionControl versionControl,
        IProcessRunner processRunner,
        IHoneEventSink eventSink)
    {
        _implementerAgent = implementerAgent;
        _versionControl = versionControl;
        _processRunner = processRunner;
        _eventSink = eventSink;
    }

    /// <inheritdoc />
    public async Task<FixStepResult> InvokeFixAgentAsync(FixStepInput input, CancellationToken ct)
    {
        ImplementerResult result = await _implementerAgent.ImplementAsync(
            input.FilePath,
            input.Explanation,
            targetLabel: input.TargetName ?? Path.GetFileName(input.TargetDir) ?? "target",
            workingDirectory: input.TargetDir,
            attempt: input.Attempt,
            previousErrors: input.PreviousErrors,
            currentFileContent: input.CurrentFileContent,
            rootCauseDocument: input.RootCauseDocument,
            ct).ConfigureAwait(false);

        return new FixStepResult(
            Success: result.Success,
            CodeBlock: result.CodeBlock,
            PromptPath: null,
            ResponsePath: null,
            AttemptPromptPath: null,
            AttemptResponsePath: null);
    }

    /// <inheritdoc />
    public async Task<ApplyStepResult> ApplySuggestionAsync(ApplyStepInput input, CancellationToken ct)
    {
        string fullPath = Path.Combine(input.TargetDir, input.FilePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(fullPath, input.NewContent, ct).ConfigureAwait(false);

        string commitMessage = string.Create(
            CultureInfo.InvariantCulture,
            $"hone(experiment-{input.Experiment}): {input.Description}");

        await _versionControl.CommitAsync(
            input.TargetDir, commitMessage, [input.FilePath], ct).ConfigureAwait(false);

        return new ApplyStepResult(Success: true, CommitSha: null, Description: input.Description);
    }

    /// <inheritdoc />
    public async Task<BuildStepResult> BuildProjectAsync(BuildStepInput input, CancellationToken ct)
    {
        ProcessResult result = await _processRunner.RunAsync(
            "dotnet",
            ["build", "--no-restore", "-c", "Release"],
            input.TargetDir,
            timeout: TimeSpan.FromMinutes(5),
            ct).ConfigureAwait(false);

        return new BuildStepResult(Success: result.Success, Output: result.Output);
    }

    /// <inheritdoc />
    public async Task<TestStepResult> RunTestsAsync(TestStepInput input, CancellationToken ct)
    {
        ProcessResult result = await _processRunner.RunAsync(
            "dotnet",
            ["test", "--no-build", "-c", "Release"],
            input.TargetDir,
            timeout: TimeSpan.FromMinutes(10),
            ct).ConfigureAwait(false);

        return new TestStepResult(Success: result.Success, Output: result.Output);
    }

    /// <inheritdoc />
    public async Task RevertForRetryAsync(RevertInput input, CancellationToken ct)
    {
        await _versionControl.RevertLastCommitAsync(input.TargetDir, ct).ConfigureAwait(false);

        _eventSink.Emit(new StatusMessage(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Reverted last commit on branch '{input.BranchName}' for retry"),
            LogLevel.Info,
            DateTimeOffset.UtcNow,
            input.Experiment));
    }

    /// <inheritdoc />
    public async Task<int> GetDiffLineCountAsync(string workingDir, CancellationToken ct)
    {
        ProcessResult result = await _processRunner.RunAsync(
            "git",
            ["diff", "--stat", "HEAD~1"],
            workingDir,
            timeout: null,
            ct).ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
        {
            return 0;
        }

        // The last line of --stat output looks like:
        // "3 files changed, 25 insertions(+), 10 deletions(-)"
        string[] lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return 0;
        }

        string summaryLine = lines[^1];
        int total = 0;

        // Parse insertions
        int insIdx = summaryLine.IndexOf("insertion", StringComparison.OrdinalIgnoreCase);
        if (insIdx > 0)
        {
            total += ParseNumberBefore(summaryLine, insIdx);
        }

        // Parse deletions
        int delIdx = summaryLine.IndexOf("deletion", StringComparison.OrdinalIgnoreCase);
        if (delIdx > 0)
        {
            total += ParseNumberBefore(summaryLine, delIdx);
        }

        return total;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDir, CancellationToken ct)
    {
        ProcessResult result = await _processRunner.RunAsync(
            "git",
            ["diff", "--name-only", "HEAD~1"],
            workingDir,
            timeout: null,
            ct).ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
        {
            return [];
        }

        return [.. result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),];
    }

    /// <summary>
    /// Scans backwards from <paramref name="beforeIndex"/> to find the number token
    /// in the git diff --stat summary line.
    /// </summary>
    private static int ParseNumberBefore(string line, int beforeIndex)
    {
        int end = beforeIndex - 1;
        while (end >= 0 && line[end] == ' ')
        {
            end--;
        }

        int start = end;
        while (start >= 0 && char.IsDigit(line[start]))
        {
            start--;
        }

        start++;

        if (start > end || start < 0)
        {
            return 0;
        }

        return int.TryParse(line.AsSpan(start, end - start + 1), CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;
    }
}
