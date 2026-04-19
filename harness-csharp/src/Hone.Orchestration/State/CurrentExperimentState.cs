using System.Text.Json;
using System.Text.Json.Serialization;
using Hone.Core.Models;

namespace Hone.Orchestration.State;

/// <summary>
/// Durable record of the experiment currently owned by the run.
/// </summary>
internal sealed record CurrentExperimentState
{
    public int Number { get; init; }

    public string QueueItemId { get; init; } = string.Empty;

    public string BranchName { get; init; } = string.Empty;

    public string BaseBranch { get; init; } = string.Empty;

    public string? CandidateHeadSha { get; init; }

    public string? CleanupManifestPath { get; init; }

    public RecoveryState Phase { get; init; } = RecoveryState.ExperimentLeased;

    public ExperimentOutcome? PendingOutcome { get; init; }

    public string StartedAt { get; init; } = string.Empty;

    [JsonExtensionData]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0016:Prefer using collection abstraction instead of implementation", Justification = "JsonExtensionData requires Dictionary")]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}
