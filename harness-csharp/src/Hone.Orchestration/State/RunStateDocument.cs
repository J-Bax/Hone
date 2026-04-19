using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hone.Orchestration.State;

/// <summary>
/// Root object for <c>run-state.json</c>, the durable control-plane authority for a run.
/// </summary>
internal sealed record RunStateDocument
{
    internal const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string StableBranch { get; init; } = string.Empty;

    public string StableHeadSha { get; init; } = string.Empty;

    public RecoveryState Status { get; init; } = RecoveryState.Idle;

    public CurrentExperimentState? CurrentExperiment { get; init; }

    [JsonExtensionData]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0016:Prefer using collection abstraction instead of implementation", Justification = "JsonExtensionData requires Dictionary")]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}
