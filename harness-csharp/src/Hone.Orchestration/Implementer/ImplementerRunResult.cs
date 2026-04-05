using Hone.Core.Models;

namespace Hone.Orchestration.Implementer;

internal sealed record ImplementerRunResult(
    IterativeFixResult Result,
    string BranchName,
    string TargetFile,
    string? TargetPath,
    string? CommitSha,
    string? BaseBranch);
