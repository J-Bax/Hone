using Hone.Core.Config;
using Hone.Core.Models;

namespace Hone.Reporting.Console;

/// <summary>
/// All structured data needed to render the console results table.
/// Replaces the file I/O in <c>Show-Results.ps1</c>.
/// </summary>
internal sealed record ResultsViewModel(
    MetricSet Baseline,
    IReadOnlyList<ExperimentRow> Experiments,
    TolerancesConfig Tolerances,
    RunMetadata? Metadata = null,
    ConsoleCounterData? BaselineCounters = null,
    IReadOnlyList<ScenarioResult>? Scenarios = null)
{
    /// <summary>
    /// Gets the experiment rows, defaulting to an empty list.
    /// </summary>
    public IReadOnlyList<ExperimentRow> Experiments { get; init; } = Experiments ?? [];
}
