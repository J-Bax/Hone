namespace Hone.Measurement.Comparison;

/// <summary>
/// Aggregated values for a single .NET runtime counter.
/// </summary>
public sealed record CounterStatistic(
    double Avg,
    double Min,
    double Max,
    double Last,
    int Samples);
