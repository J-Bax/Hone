namespace Hone.Orchestration.Failure;

internal sealed record FailureHandlerResult(
    bool Success,
    bool CleanupSucceeded,
    bool VerificationSucceeded,
    bool MetadataUpdated,
    bool QueueMarked,
    string CleanupManifestPath,
    IReadOnlyList<string> TrackedPaths,
    IReadOnlyList<string> UntrackedPaths,
    string? ExpectedStableHeadSha,
    string? ObservedHeadSha,
    string? ObservedBranch,
    bool WorktreeCleanAfterCleanup,
    string? FailureMessage);
