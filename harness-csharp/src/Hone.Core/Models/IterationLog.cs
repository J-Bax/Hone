namespace Hone.Core.Models;

/// <summary>
/// Log of all attempts within an iterative fix cycle.
/// </summary>
public sealed record IterationLog(
    IReadOnlyList<IterationAttempt> Attempts)
{
    /// <summary>
    /// Gets the iteration attempts, defaulting to an empty list.
    /// </summary>
    public IReadOnlyList<IterationAttempt> Attempts { get; init; } = Attempts ?? [];
}
