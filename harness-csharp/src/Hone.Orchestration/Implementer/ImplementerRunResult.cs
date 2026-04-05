using Hone.Core.Models;

namespace Hone.Orchestration.Implementer;

/// <summary>
/// Extended result returned by <see cref="IterativeImplementerRunner"/>
/// wrapping <see cref="IterativeFixResult"/> with additional context.
/// </summary>
internal sealed record ImplementerRunResult(
    IterativeFixResult Result,
    string BranchName,
    string TargetFile,
    string? TargetPath,
    string? CommitSha,
    string? BaseBranch);
