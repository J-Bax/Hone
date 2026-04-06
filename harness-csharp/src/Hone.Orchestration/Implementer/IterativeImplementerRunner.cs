using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hone.Core.Models;
using Hone.Core.Observability;

namespace Hone.Orchestration.Implementer;

/// <summary>
/// Orchestrates the iterative fix/apply/build/test cycle for a single experiment.
/// </summary>
internal sealed class IterativeImplementerRunner
{
    private const int MaxErrorTextLength = 4000;
    private const int MaxDescriptionLength = 120;

    private static readonly JsonSerializerOptions LogJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IImplementerPipeline _pipeline;
    private readonly IHoneEventSink _eventSink;

    internal IterativeImplementerRunner(IImplementerPipeline pipeline, IHoneEventSink eventSink)
    {
        _pipeline = pipeline;
        _eventSink = eventSink;
    }

    /// <summary>
    /// Runs the iterative fix cycle for one experiment.
    /// </summary>
    internal async Task<ImplementerRunResult> RunAsync(
        ImplementerOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        int maxAttempts = Math.Max(options.Config.MaxAttempts, 1);
        bool iterativeMode = maxAttempts > 1;
        bool testFileGuardEnabled = iterativeMode && options.Config.TestFileGuard;
        double diffGrowthFactor = options.Config.MaxDiffGrowthFactor;
        bool diffGrowthGuardEnabled = iterativeMode && diffGrowthFactor > 0;
        IReadOnlyList<string> testGuardRoots = testFileGuardEnabled
            ? NormalizeGuardRoots(options.TestProjectPaths)
            : [];

        string branchName = $"{options.BranchPrefix}-{options.Experiment}";
        string targetFile = ResolveTargetFile(options.FilePath, options.TargetName);
        string fullTargetPath = Path.Combine(options.TargetDir, targetFile);

        // If the resolved path doesn't exist, try the original (unstripped) path.
        // This handles cases where targetName matches a real directory in the repo.
        if (!File.Exists(fullTargetPath))
        {
            string originalFull = Path.Combine(options.TargetDir, options.FilePath.Trim());
            if (File.Exists(originalFull))
            {
                targetFile = options.FilePath.Trim();
                fullTargetPath = originalFull;
            }
        }

        // Validate target directory exists
        string? parentDir = Path.GetDirectoryName(fullTargetPath);
        if (parentDir is null || !Directory.Exists(parentDir))
        {
            string detail = $"Target path could not be resolved: {targetFile}";
            _eventSink.Emit(new StatusMessage(
                $"Invalid experiment target path: {targetFile}",
                LogLevel.Warning,
                DateTimeOffset.UtcNow,
                options.Experiment));

            return BuildFinalResult(
                options, branchName, targetFile, fullTargetPath,
                commitSha: null,
                success: false, exitReason: "invalid_target",
                failureDetail: detail, entries: []);
        }

        // Create experiment and iteration log directories
        string experimentDir = Path.Combine(
            options.TargetDir, options.ResultsPath, $"experiment-{options.Experiment}");
        Directory.CreateDirectory(experimentDir);

        var entries = new List<AttemptLogEntry>();
        int? firstAttemptDiffLines = null;
        string? previousErrors = null;
        string? currentFileContent = null;
        string? lastCommitSha = null;
        string? lastFailureDetail = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var timer = Stopwatch.StartNew();
            string attemptDir = Path.Combine(experimentDir, "iterations", $"attempt-{attempt}");
            Directory.CreateDirectory(attemptDir);

            if (iterativeMode)
            {
                _eventSink.Emit(new StatusMessage(
                    $"Attempt {attempt}/{maxAttempts}",
                    LogLevel.Info,
                    DateTimeOffset.UtcNow,
                    options.Experiment));
            }

            // ── Fix ─────────────────────────────────────────────────────────
            FixStepResult fixResult = await _pipeline.InvokeImplementerAgentAsync(
                new FixStepInput(
                    targetFile, options.Explanation, options.RootCauseDocument,
                    options.Experiment, options.TargetName, options.TargetDir,
                    attempt, previousErrors, currentFileContent),
                ct).ConfigureAwait(false);

            if (!fixResult.Success || fixResult.CodeBlock is null)
            {
                timer.Stop();
                string detail = "Fix agent response did not contain a code block.";
                entries.Add(new AttemptLogEntry(
                    attempt, "fix", "failed",
                    RoundDuration(timer), DiffLines: 0,
                    Error: detail,
                    Artifacts: BuildArtifacts(
                        fixResult.PromptPath, fixResult.ResponsePath,
                        options.TargetDir)));

                return BuildFinalResult(
                    options, branchName, targetFile, fullTargetPath,
                    lastCommitSha, success: false, exitReason: "fix_failed",
                    failureDetail: detail, entries);
            }

            // ── Apply ───────────────────────────────────────────────────────
            ApplyStepResult applyResult = await _pipeline.ApplySuggestionAsync(
                new ApplyStepInput(
                    targetFile, fixResult.CodeBlock,
                    TruncateString(options.Explanation, MaxDescriptionLength),
                    options.Experiment, options.BaseBranch, options.TargetDir),
                ct).ConfigureAwait(false);

            if (!applyResult.Success)
            {
                timer.Stop();
                string detail = applyResult.Description
                    ?? "Failed to apply suggested code change.";
                entries.Add(new AttemptLogEntry(
                    attempt, "apply", "failed",
                    RoundDuration(timer), DiffLines: 0,
                    Error: detail,
                    Artifacts: BuildArtifacts(
                        fixResult.AttemptPromptPath, fixResult.AttemptResponsePath,
                        options.TargetDir)));

                return BuildFinalResult(
                    options, branchName, targetFile, fullTargetPath,
                    applyResult.CommitSha ?? lastCommitSha,
                    success: false, exitReason: "apply_failed",
                    failureDetail: detail, entries);
            }

            lastCommitSha = applyResult.CommitSha;

            // ── Diff + artifacts ────────────────────────────────────────────
            int diffLines = await _pipeline.GetDiffLineCountAsync(
                options.TargetDir, ct).ConfigureAwait(false);
            firstAttemptDiffLines ??= Math.Max(diffLines, 1);

            Dictionary<string, string?> artifacts = BuildArtifacts(
                fixResult.AttemptPromptPath, fixResult.AttemptResponsePath,
                options.TargetDir);

            // ── Test file guard ─────────────────────────────────────────────
            if (testFileGuardEnabled)
            {
                IReadOnlyList<string> changedFiles = await _pipeline.GetChangedFilesAsync(
                    options.TargetDir, ct).ConfigureAwait(false);
                IReadOnlyList<string> changedTestFiles =
                    GetChangedTestFiles(changedFiles, testGuardRoots);

                if (changedTestFiles.Count > 0)
                {
                    lastFailureDetail = $"Fix modified test files: {string.Join(", ", changedTestFiles)}";
                    bool canRetry = HandleStepFailure(
                        "guard", "rejected", lastFailureDetail, lastFailureDetail,
                        timer, attempt, maxAttempts, diffLines, applyResult.CommitSha,
                        artifacts, entries, out lastFailureDetail, out previousErrors,
                        changedFiles: [.. changedTestFiles]);

                    currentFileContent = await ReadFileContentAsync(fullTargetPath, ct)
                        .ConfigureAwait(false);

                    if (canRetry)
                    {
                        await _pipeline.RevertForRetryAsync(
                            new RevertInput(branchName, targetFile,
                                options.Experiment, options.TargetDir),
                            ct).ConfigureAwait(false);
                        continue;
                    }

                    return BuildFinalResult(
                        options, branchName, targetFile, fullTargetPath,
                        lastCommitSha, success: false,
                        exitReason: "retry_budget_exhausted",
                        failureDetail: lastFailureDetail, entries);
                }
            }

            // ── Diff growth guard ───────────────────────────────────────────
            if (diffGrowthGuardEnabled && attempt > 1
                && diffLines > (firstAttemptDiffLines.Value * diffGrowthFactor))
            {
                string growthDetail = $"Diff grew to {diffLines} lines " +
                    $"(limit: {Math.Round(firstAttemptDiffLines.Value * diffGrowthFactor, 2)}).";
                bool canRetry = HandleStepFailure(
                    "guard", "rejected", growthDetail, growthDetail,
                    timer, attempt, maxAttempts, diffLines, applyResult.CommitSha,
                    artifacts, entries, out lastFailureDetail, out previousErrors);

                currentFileContent = await ReadFileContentAsync(fullTargetPath, ct)
                    .ConfigureAwait(false);

                if (canRetry)
                {
                    await _pipeline.RevertForRetryAsync(
                        new RevertInput(branchName, targetFile,
                            options.Experiment, options.TargetDir),
                        ct).ConfigureAwait(false);
                    continue;
                }

                return BuildFinalResult(
                    options, branchName, targetFile, fullTargetPath,
                    lastCommitSha, success: false,
                    exitReason: "retry_budget_exhausted",
                    failureDetail: lastFailureDetail, entries);
            }

            // ── Build ───────────────────────────────────────────────────────
            string buildLogPath = Path.Combine(attemptDir, "build.log");
            BuildStepResult buildResult = await _pipeline.BuildProjectAsync(
                new BuildStepInput(
                    options.TargetDir, options.Experiment, attempt, buildLogPath),
                ct).ConfigureAwait(false);
            artifacts["buildLog"] = ToRelativePath(buildLogPath, options.TargetDir);

            if (!buildResult.Success)
            {
                bool canRetry = HandleStepFailure(
                    "build", "failed", LimitErrorText(buildResult.Output), buildResult.Output,
                    timer, attempt, maxAttempts, diffLines, applyResult.CommitSha,
                    artifacts, entries, out lastFailureDetail, out previousErrors);

                currentFileContent = await ReadFileContentAsync(fullTargetPath, ct)
                    .ConfigureAwait(false);

                if (canRetry)
                {
                    await _pipeline.RevertForRetryAsync(
                        new RevertInput(branchName, targetFile,
                            options.Experiment, options.TargetDir),
                        ct).ConfigureAwait(false);
                    continue;
                }

                string buildExitReason = iterativeMode
                    ? "retry_budget_exhausted"
                    : "build_failure";
                return BuildFinalResult(
                    options, branchName, targetFile, fullTargetPath,
                    lastCommitSha, success: false, exitReason: buildExitReason,
                    failureDetail: lastFailureDetail, entries);
            }

            // ── Tests ───────────────────────────────────────────────────────
            string testLogPath = Path.Combine(attemptDir, "e2e-tests.log");
            string testTrxPath = Path.Combine(attemptDir, "e2e-results.trx");
            TestStepResult testResult = await _pipeline.RunTestsAsync(
                new TestStepInput(
                    options.TargetDir, options.Experiment, attempt,
                    testLogPath, testTrxPath),
                ct).ConfigureAwait(false);
            artifacts["e2eLog"] = ToRelativePath(testLogPath, options.TargetDir);
            artifacts["e2eResults"] = ToRelativePath(testTrxPath, options.TargetDir);

            if (!testResult.Success)
            {
                bool canRetry = HandleStepFailure(
                    "test", "failed", LimitErrorText(testResult.Output), testResult.Output,
                    timer, attempt, maxAttempts, diffLines, applyResult.CommitSha,
                    artifacts, entries, out lastFailureDetail, out previousErrors);

                currentFileContent = await ReadFileContentAsync(fullTargetPath, ct)
                    .ConfigureAwait(false);

                if (canRetry)
                {
                    await _pipeline.RevertForRetryAsync(
                        new RevertInput(branchName, targetFile,
                            options.Experiment, options.TargetDir),
                        ct).ConfigureAwait(false);
                    continue;
                }

                string testExitReason = iterativeMode
                    ? "retry_budget_exhausted"
                    : "test_failure";
                return BuildFinalResult(
                    options, branchName, targetFile, fullTargetPath,
                    lastCommitSha, success: false, exitReason: testExitReason,
                    failureDetail: lastFailureDetail, entries);
            }

            // ── Success ─────────────────────────────────────────────────────
            timer.Stop();
            entries.Add(new AttemptLogEntry(
                attempt, "test", "passed",
                RoundDuration(timer), diffLines,
                CommitSha: applyResult.CommitSha,
                Artifacts: artifacts));

            return BuildFinalResult(
                options, branchName, targetFile, fullTargetPath,
                lastCommitSha, success: true, exitReason: "success",
                failureDetail: null, entries);
        }

        // Safety net — should not normally be reached
        return BuildFinalResult(
            options, branchName, targetFile, fullTargetPath,
            lastCommitSha, success: false,
            exitReason: "retry_budget_exhausted",
            failureDetail: lastFailureDetail, entries);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Stops the timer, records the failure in the attempt log, and returns
    /// <see langword="true"/> if the caller should retry (i.e. attempt &lt; maxAttempts).
    /// </summary>
    private static bool HandleStepFailure(
        string stepName, string stepOutcome,
        string? errorDetail, string? rawErrorForRetry,
        Stopwatch timer, int attempt, int maxAttempts,
        int diffLines, string? commitSha,
        Dictionary<string, string?> artifacts, List<AttemptLogEntry> entries,
        out string? failureDetail, out string retryContext,
        IReadOnlyList<string>? changedFiles = null)
    {
        timer.Stop();
        failureDetail = errorDetail;
        retryContext = FormatRetryContext(attempt, stepName, rawErrorForRetry ?? failureDetail ?? string.Empty);

        entries.Add(new AttemptLogEntry(
            attempt, stepName, stepOutcome,
            RoundDuration(timer), diffLines,
            Error: failureDetail,
            CommitSha: commitSha,
            ChangedFiles: changedFiles,
            Artifacts: artifacts));

        return attempt < maxAttempts;
    }

    private static ImplementerRunResult BuildFinalResult(
        ImplementerOptions options, string branchName,
        string targetFile, string fullTargetPath,
        string? commitSha, bool success, string exitReason,
        string? failureDetail,
        List<AttemptLogEntry> entries)
    {
        string experimentDir = Path.Combine(
            options.TargetDir, options.ResultsPath,
            $"experiment-{options.Experiment}");
        string iterationLogPath = Path.Combine(experimentDir, "iteration-log.json");

        string finalOutcome = success ? "success" : exitReason;
        var document = new IterationLogDocument(
            options.Experiment, entries.Count, finalOutcome, entries);

        WriteIterationLog(iterationLogPath, document);

        string? logRelativePath = ToRelativePath(iterationLogPath, options.TargetDir);

        var iterationLog = new IterationLog(
            [.. entries.Select(e => new IterationAttempt(
                e.Attempt, e.Stage, e.Outcome, e.DiffLines)),]);

        var result = new IterativeFixResult(
            success, entries.Count, exitReason, failureDetail,
            iterationLog, logRelativePath);

        return new ImplementerRunResult(
            result, branchName, targetFile, fullTargetPath,
            commitSha, options.BaseBranch);
    }

    private static void WriteIterationLog(
        string path, IterationLogDocument document)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(document, LogJsonOptions);

        // Use FileStream for async-compatible write that avoids the banned
        // File.WriteAllText overload while keeping the implementation simple.
        using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(json);
    }

    internal static string ResolveTargetFile(string candidatePath, string? targetName)
    {
        string targetFile = candidatePath.Trim();
        if (string.IsNullOrEmpty(targetName))
        {
            return targetFile;
        }

        string prefixSlash = targetName + "/";
        string prefixBackslash = targetName + "\\";

        if (targetFile.StartsWith(prefixSlash, StringComparison.Ordinal))
        {
            return targetFile[prefixSlash.Length..];
        }

        if (targetFile.StartsWith(prefixBackslash, StringComparison.Ordinal))
        {
            return targetFile[prefixBackslash.Length..];
        }

        return targetFile;
    }

    internal static string? LimitErrorText(string? text, int maxLength = MaxErrorTextLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        string trimmed = text.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        return trimmed[..maxLength] + "\n... [truncated]";
    }

    internal static string FormatRetryContext(int attempt, string? stage, string? errorOutput)
    {
        string safeStage = string.IsNullOrEmpty(stage) ? "unknown" : stage;
        string safeOutput = string.IsNullOrWhiteSpace(errorOutput)
            ? "No error output was captured."
            : errorOutput.Trim();

        return $"Attempt {attempt} failed at the {safeStage} stage.\n\n{safeOutput}";
    }

    internal static string? ToRelativePath(string? path, string root)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(path);

        if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath[fullRoot.Length..]
                .TrimStart(Path.DirectorySeparatorChar)
                .Replace('\\', '/');
        }

        return path.Replace('\\', '/');
    }

    internal static IReadOnlyList<string> NormalizeGuardRoots(
        IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return [];
        }

        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string value = raw.Replace('\\', '/')
                .Trim()
                .TrimStart('.')
                .TrimStart('/')
                .TrimEnd('/');

            if (value.Length > 0)
            {
                _ = normalized.Add(value);
            }
        }

        return [.. normalized];
    }

    internal static IReadOnlyList<string> GetChangedTestFiles(
        IReadOnlyList<string> changedFiles, IReadOnlyList<string> guardRoots)
    {
        if (changedFiles.Count == 0 || guardRoots.Count == 0)
        {
            return [];
        }

        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in changedFiles)
        {
            string normalizedFile = file.Replace('\\', '/')
                .TrimStart('.')
                .TrimStart('/');

            foreach (string root in guardRoots)
            {
                if (normalizedFile.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || normalizedFile.StartsWith(
                        root + "/", StringComparison.OrdinalIgnoreCase))
                {
                    _ = matched.Add(file);
                    break;
                }
            }
        }

        return [.. matched];
    }

    private static string TruncateString(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private static double RoundDuration(Stopwatch timer) =>
        Math.Round(timer.Elapsed.TotalSeconds, 2);

    private static Dictionary<string, string?> BuildArtifacts(
        string? promptPath, string? responsePath, string targetDir) =>
        new(StringComparer.Ordinal)
        {
            ["fixPrompt"] = ToRelativePath(promptPath, targetDir),
            ["fixResponse"] = ToRelativePath(responsePath, targetDir),
        };

    private static async Task<string?> ReadFileContentAsync(
        string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
    }
}
