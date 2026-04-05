namespace Hone.Reporting.Console;

/// <summary>
/// Per-scenario baseline and experiment data for the scenario breakdown section.
/// </summary>
internal sealed record ScenarioResult(
    string ScenarioName,
    ExperimentRow Baseline,
    IReadOnlyList<ExperimentRow> Experiments)
{
    /// <summary>
    /// Gets the experiment rows, defaulting to an empty list.
    /// </summary>
    public IReadOnlyList<ExperimentRow> Experiments { get; init; } = Experiments ?? [];
}
