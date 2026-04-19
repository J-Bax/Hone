namespace Hone.Orchestration.State;

/// <summary>
/// Recovery-relevant control-plane states persisted in <c>run-state.json</c>.
/// </summary>
internal enum RecoveryState
{
    Idle,
    ExperimentLeased,
    BranchCreated,
    CandidateCommitted,
    Finalizing,
    RepairRequired,
}
