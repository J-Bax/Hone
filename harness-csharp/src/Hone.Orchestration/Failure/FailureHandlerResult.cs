namespace Hone.Orchestration.Failure;

internal sealed record FailureHandlerResult(
    bool Success,
    bool RevertSucceeded,
    bool MetadataUpdated,
    bool QueueMarked);
