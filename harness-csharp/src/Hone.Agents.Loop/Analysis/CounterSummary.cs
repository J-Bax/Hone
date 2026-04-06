namespace Hone.Agents.Loop.Analysis;

/// <summary>
/// Lightweight summary of .NET runtime counter values for prompt rendering.
/// Avoids coupling <c>Hone.Agents.Loop</c> to <c>Hone.Measurement</c>.
/// The orchestration layer converts <c>RuntimeCounterMetrics</c> → <see cref="CounterSummary"/>.
/// </summary>
public sealed record CounterSummary(
    string CpuAvg,
    string GcHeapMax,
    string Gen2Collections,
    string ThreadPoolMaxThreads);
