namespace Hone.Core.Contracts;

/// <summary>
/// Options for collecting runtime metrics from a process.
/// </summary>
public sealed record RuntimeMetricsOptions(
    IReadOnlyList<string> Providers,
    int RefreshIntervalSeconds);
