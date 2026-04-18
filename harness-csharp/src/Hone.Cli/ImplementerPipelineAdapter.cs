using System.Globalization;

using Hone.Agents.Loop.Critic;
using Hone.Agents.Loop.Implementer;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Lifecycle.Hooks;
using Hone.Orchestration.Implementer;

namespace Hone.Cli;

/// <summary>
/// Wires the real service implementations to the <see cref="IImplementerPipeline"/> contract.
/// </summary>
internal sealed class ImplementerPipelineAdapter : IImplementerPipeline
{
    private readonly ImplementerAgent _implementerAgent;
    private readonly CriticAgent _criticAgent;
    private readonly IVersionControl _versionControl;
    private readonly IProcessRunner _processRunner;
    private readonly IHoneEventSink _eventSink;
    private readonly HoneConfig _config;
    private readonly LifecycleHookDispatcher? _hookDispatcher;
    private readonly TargetConfig? _targetConfig;

    internal ImplementerPipelineAdapter(
        ImplementerAgent implementerAgent,
        CriticAgent criticAgent,
        IVersionControl versionControl,
        IProcessRunner processRunner,
        IHoneEventSink eventSink,
        HoneConfig config,
        LifecycleHookDispatcher? hookDispatcher = null,
        TargetConfig? targetConfig = null)
    {
        _implementerAgent = implementerAgent;
        _criticAgent = criticAgent;
        _versionControl = versionControl;
        _processRunner = processRunner;
        _eventSink = eventSink;
        _config = config;
        _hookDispatcher = hookDispatcher;
        _targetConfig = targetConfig;
    }

    /// <inheritdoc />
    public async Task<FixStepResult> InvokeImplementerAgentAsync(FixStepInput input, CancellationToken ct)
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
        string branchName = GetExperimentBranchName(input.Experiment);
        await EnsureExperimentBranchAsync(input.TargetDir, input.BaseBranch, branchName, ct)
            .ConfigureAwait(false);

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

    private async Task EnsureExperimentBranchAsync(
        string targetDir,
        string baseBranch,
        string branchName,
        CancellationToken ct)
    {
        string currentBranch = await _versionControl.GetCurrentBranchAsync(targetDir, ct)
            .ConfigureAwait(false);

        if (string.Equals(currentBranch, branchName, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.Equals(currentBranch, baseBranch, StringComparison.Ordinal))
        {
            await _versionControl.CheckoutAsync(targetDir, baseBranch, create: false, ct)
                .ConfigureAwait(false);
        }

        bool branchExists = await BranchExistsAsync(targetDir, branchName, ct)
            .ConfigureAwait(false);

        await _versionControl.CheckoutAsync(targetDir, branchName, create: !branchExists, ct)
            .ConfigureAwait(false);
    }

    private async Task<bool> BranchExistsAsync(string targetDir, string branchName, CancellationToken ct)
    {
        ProcessResult result = await _processRunner.RunAsync(
            "git",
            ["show-ref", "--verify", "--quiet", $"refs/heads/{branchName}"],
            targetDir,
            timeout: null,
            ct).ConfigureAwait(false);

        return result.Success;
    }

    private string GetExperimentBranchName(int experiment) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{_config.Loop.BranchPrefix}-{experiment}");

    /// <inheritdoc />
    public async Task<BuildStepResult> BuildProjectAsync(BuildStepInput input, CancellationToken ct)
    {
        if (_hookDispatcher is not null && _targetConfig is not null)
        {
            ResolvedHook hook = HookResolver.Resolve("Build", _targetConfig);
            var context = new HookContext(input.TargetDir, _config, BaseUrl: null, input.Experiment);
            HookResult hookResult = await _hookDispatcher.DispatchAsync("Build", hook, context, ct)
                .ConfigureAwait(false);
            return new BuildStepResult(Success: hookResult.Success, Output: hookResult.Message);
        }

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
        if (_hookDispatcher is not null && _targetConfig is not null)
        {
            ResolvedHook hook = HookResolver.Resolve("Test", _targetConfig);
            var context = new HookContext(input.TargetDir, _config, BaseUrl: null, input.Experiment);
            HookResult hookResult = await _hookDispatcher.DispatchAsync("Test", hook, context, ct)
                .ConfigureAwait(false);
            return new TestStepResult(Success: hookResult.Success, Output: hookResult.Message);
        }

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
        // Exclude .hone/ results artifacts so diff count reflects only source changes
        ProcessResult result = await _processRunner.RunAsync(
            "git",
            ["diff", "--stat", "HEAD~1", "--", ".", ":(exclude).hone"],
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

    /// <inheritdoc />
    public async Task<CriticStepResult> InvokeCriticAgentAsync(CriticStepInput input, CancellationToken ct)
    {
        CriticResult result = await _criticAgent.ReviewAsync(
            input.FilePath,
            input.Explanation,
            input.Diff,
            input.ClassificationScope,
            targetLabel: input.TargetName ?? Path.GetFileName(input.TargetDir) ?? "target",
            workingDirectory: input.TargetDir,
            ct).ConfigureAwait(false);

        // Persist raw critic response alongside other attempt artifacts
        string? responsePath = null;
        if (input.AdditionalResponsePath is not null)
        {
            string? dir = Path.GetDirectoryName(input.AdditionalResponsePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(input.AdditionalResponsePath, result.Response, ct)
                .ConfigureAwait(false);
            responsePath = input.AdditionalResponsePath;
        }

        return new CriticStepResult(
            Success: result.Success,
            Approved: result.Approved,
            Feedback: result.Feedback,
            Summary: result.Summary,
            Confidence: result.Confidence,
            ResponsePath: responsePath);
    }

    /// <inheritdoc />
    public async Task<string> GetDiffContentAsync(string workingDir, string filePath, CancellationToken ct)
    {
        ProcessResult result = await _processRunner.RunAsync(
            "git",
            ["diff", "HEAD~1", "--", filePath],
            workingDir,
            timeout: null,
            ct).ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
        {
            return string.Empty;
        }

        return result.Output;
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
