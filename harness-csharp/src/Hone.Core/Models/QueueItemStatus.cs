namespace Hone.Core.Models;

/// <summary>
/// Status of an item in the work queue.
/// </summary>
public enum QueueItemStatus
{
    /// <summary>The item is waiting to be processed.</summary>
    Pending,

    /// <summary>The item is currently being processed.</summary>
    InProgress,

    /// <summary>The item has been processed successfully.</summary>
    Done,

    /// <summary>The item was skipped.</summary>
    Skipped,
}
