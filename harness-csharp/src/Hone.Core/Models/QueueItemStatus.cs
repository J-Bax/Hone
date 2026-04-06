namespace Hone.Core.Models;

/// <summary>
/// Status of an item in the work queue.
/// </summary>
public enum QueueItemStatus
{
    /// <summary>Uninitialized or unknown status.</summary>
    Unknown = 0,

    /// <summary>The item is waiting to be processed.</summary>
    Pending = 1,

    /// <summary>The item is currently being processed.</summary>
    InProgress = 2,

    /// <summary>The item has been processed successfully.</summary>
    Done = 3,

    /// <summary>The item was skipped.</summary>
    Skipped = 4,
}
