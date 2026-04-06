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
    public IReadOnlyList<ExperimentMetadata> Experiments { get; init; } = Experiments;
}
