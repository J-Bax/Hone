using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hone.Orchestration.State;

/// <summary>
/// Durable manifest describing the paths that belong to an experiment cleanup.
/// </summary>
internal sealed record CleanupManifest
{
    internal const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public int Experiment { get; init; }

    public string BranchName { get; init; } = string.Empty;

    public string BaseBranch { get; init; } = string.Empty;

    public string? CandidateHeadSha { get; init; }

    public string? ExpectedStableHeadSha { get; init; }

    public IReadOnlyList<string> TrackedPaths { get; init; } = [];

    public IReadOnlyList<string> UntrackedPaths { get; init; } = [];

    [JsonExtensionData]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0016:Prefer using collection abstraction instead of implementation", Justification = "JsonExtensionData requires Dictionary")]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}
