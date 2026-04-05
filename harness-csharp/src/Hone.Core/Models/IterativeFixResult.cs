namespace Hone.Core.Models;

/// <summary>
/// Result of running the iterative implementer.
/// </summary>
public sealed record IterativeFixResult(
    bool Success,
    int AttemptCount,
    string ExitReason,
    string? FailureDetail,
    IterationLog? IterationLog,
    string? IterationLogRelativePath);
