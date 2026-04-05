namespace Hone.Orchestration.Failure;

internal sealed record FailureContext(
    string BranchName,
    string FilePath,
    int Experiment,
    string Outcome,
    string RevertDescription,
    string TargetDir,
    string? MetadataSummary = null,
    string? MetadataFilePath = null,
    string? QueueItemId = null,
    bool SkipMetadataUpdate = false,
    bool SkipQueueMarkDone = false);
