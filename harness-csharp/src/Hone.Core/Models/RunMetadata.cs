namespace Hone.Core.Models;

/// <summary>
/// Root object for run-metadata.json.
/// </summary>
public sealed record RunMetadata(
    string TargetName,
    string StartedAt,
    MachineInfo? MachineInfo,
    IReadOnlyList<ExperimentMetadata> Experiments)
{
    /// <summary>
    /// Gets the experiment metadata entries, defaulting to an empty list.
    /// </summary>
    public IReadOnlyList<ExperimentMetadata> Experiments { get; init; } = Experiments ?? [];
}
