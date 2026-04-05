using System.Diagnostics.CodeAnalysis;

namespace Hone.Core.Models;

/// <summary>
/// Root object for the optimization work queue.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Domain name from PowerShell source")]
public sealed record OptimizationQueue(
    int GeneratedByExperiment,
    IReadOnlyList<QueueItem> Items)
{
    public IReadOnlyList<QueueItem> Items { get; init; } = Items;
}
