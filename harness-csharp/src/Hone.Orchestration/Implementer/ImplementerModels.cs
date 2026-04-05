namespace Hone.Orchestration.Implementer;

// ── Step inputs ─────────────────────────────────────────────────────────────

/// <summary>Input for the fix-agent step.</summary>
internal sealed record FixStepInput(
    string FilePath,
    string Explanation,
    string? RootCauseDocument,
    int Experiment,
    string? TargetName,
    string TargetDir,
    int Attempt,
    string? PreviousErrors,
    string? CurrentFileContent);

/// <summary>Input for the apply-suggestion step.</summary>
internal sealed record ApplyStepInput(
    string FilePath,
    string NewContent,
    string Description,
    int Experiment,
    string BaseBranch,
    string TargetDir);

/// <summary>Input for the build step.</summary>
internal sealed record BuildStepInput(
    string TargetDir,
    int Experiment,
    int Attempt,
    string? AdditionalLogPath);

/// <summary>Input for the test step.</summary>
internal sealed record TestStepInput(
    string TargetDir,
    int Experiment,
    int Attempt,
    string? AdditionalLogPath,
    string? AdditionalTrxPath);

/// <summary>Input for the revert-for-retry step.</summary>
internal sealed record RevertInput(
    string BranchName,
    string FilePath,
    int Experiment,
    string TargetDir);

// ── Step results ────────────────────────────────────────────────────────────

/// <summary>Result of the fix-agent invocation.</summary>
internal sealed record FixStepResult(
    bool Success,
    string? CodeBlock,
    string? PromptPath,
    string? ResponsePath,
    string? AttemptPromptPath,
    string? AttemptResponsePath);

/// <summary>Result of applying the suggested code change.</summary>
internal sealed record ApplyStepResult(
    bool Success,
    string? CommitSha,
    string? Description);

/// <summary>Result of a project build.</summary>
internal sealed record BuildStepResult(bool Success, string? Output);

/// <summary>Result of running end-to-end tests.</summary>
internal sealed record TestStepResult(bool Success, string? Output);

// ── Internal log types (for iteration-log.json serialisation) ───────────────

/// <summary>One attempt entry written to <c>iteration-log.json</c>.</summary>
internal sealed record AttemptLogEntry(
    int Attempt,
    string Stage,
    string Outcome,
    double DurationSec,
    int DiffLines,
    string? Error = null,
    string? CommitSha = null,
    IReadOnlyList<string>? ChangedFiles = null,
    Dictionary<string, string?>? Artifacts = null);

/// <summary>Top-level document for <c>iteration-log.json</c>.</summary>
internal sealed record IterationLogDocument(
    int Experiment,
    int TotalAttempts,
    string FinalOutcome,
    IReadOnlyList<AttemptLogEntry> Attempts);
