namespace Hone.Orchestration.Failure;

/// <summary>
/// Outcome of <see cref="ExperimentFailureHandler.HandleFailureAsync"/>.
/// <see cref="Success"/> mirrors the revert result (matching the PowerShell contract).
/// </summary>
internal sealed record FailureHandlerResult(
    bool Success,
    bool RevertSucceeded,
    bool MetadataUpdated,
    bool QueueMarked);
