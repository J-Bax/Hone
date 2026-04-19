namespace Hone.Orchestration.Failure;

internal sealed record FailureContext(
    string BranchName,
    string BaseBranch,
    string FilePath,
    int Experiment,
    string Outcome,
    string RevertDescription,
    string TargetDir,
    string? ExpectedStableHeadSha = null,
    string? CleanupManifestPath = null,
    IReadOnlyList<string>? KnownUntrackedPaths = null,
    string? MetadataSummary = null,
    string? MetadataFilePath = null,
    string? QueueItemId = null,
    bool SkipMetadataUpdate = false,
    bool SkipQueueMarkDone = false,
    string? ResultsPath = null);
