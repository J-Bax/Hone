namespace Hone.Orchestration.Failure;

/// <summary>
/// Captures all information needed to handle a failed experiment:
/// which branch, file, experiment number, outcome, and whether to skip
/// metadata or queue updates.
/// </summary>
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
