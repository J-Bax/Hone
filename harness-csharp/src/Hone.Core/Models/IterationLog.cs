namespace Hone.Core.Models;

/// <summary>
/// Log of all attempts within an iterative fix cycle.
/// </summary>
public sealed record IterationLog(
    IReadOnlyList<IterationAttempt> Attempts)
{
    public IReadOnlyList<IterationAttempt> Attempts { get; init; } = Attempts;
}
