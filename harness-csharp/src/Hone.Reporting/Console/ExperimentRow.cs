using Hone.Core.Models;

namespace Hone.Reporting.Console;

/// <summary>
/// Per-experiment row data for the console results table.
/// </summary>
public sealed record ExperimentRow(
    int Experiment,
    MetricSet Metrics,
    ConsoleCounterData? Counters = null);
