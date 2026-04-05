namespace Hone.Core.Models;

/// <summary>
/// A single attempt within an iterative fix cycle.
/// </summary>
public sealed record IterationAttempt(
    int Attempt,
    string Stage,
    string Outcome,
    int DiffLines);
