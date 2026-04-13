namespace Hone.Orchestration.Implementer;

// ── Step inputs ─────────────────────────────────────────────────────────────

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

internal sealed record ApplyStepInput(
    string FilePath,
    string NewContent,
    string Description,
    int Experiment,
    string BaseBranch,
    string TargetDir);

internal sealed record BuildStepInput(
    string TargetDir,
    int Experiment,
    int Attempt,
    string? AdditionalLogPath);

internal sealed record TestStepInput(
    string TargetDir,
    int Experiment,
    int Attempt,
    string? AdditionalLogPath,
    string? AdditionalTrxPath);

internal sealed record RevertInput(
    string BranchName,
    string FilePath,
    int Experiment,
    string TargetDir);

internal sealed record CriticStepInput(
    string FilePath,
    string Explanation,
    string Diff,
    string? ClassificationScope,
    string? TargetName,
    string TargetDir,
    int Experiment,
    int Attempt,
    string? AdditionalResponsePath);

// ── Step results ────────────────────────────────────────────────────────────

internal sealed record FixStepResult(
    bool Success,
    string? CodeBlock,
    string? PromptPath,
    string? ResponsePath,
    string? AttemptPromptPath,
    string? AttemptResponsePath);

internal sealed record ApplyStepResult(
    bool Success,
    string? CommitSha,
    string? Description);

internal sealed record BuildStepResult(bool Success, string? Output);

internal sealed record TestStepResult(bool Success, string? Output);

internal sealed record CriticStepResult(
    bool Success,
    bool Approved,
    string? Feedback,
    string? Summary,
    string? Confidence,
    string? ResponsePath);

// ── Internal log types (for iteration-log.json serialisation) ───────────────

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

internal sealed record IterationLogDocument(
    int Experiment,
    int TotalAttempts,
    string FinalOutcome,
    IReadOnlyList<AttemptLogEntry> Attempts);
