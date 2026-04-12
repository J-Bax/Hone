namespace Hone.Core.Observability;

/// <summary>
/// Receives and processes Hone lifecycle events.
/// </summary>
public interface IHoneEventSink
{
    /// <summary>
    /// Processes a single harness event.
    /// </summary>
    /// <remarks>
    /// Implementations must be safe for concurrent use because <see cref="Emit(HoneEvent)"/>
    /// may be called from multiple threads when analyzers execute in parallel.
    /// </remarks>
    /// <param name="honeEvent">The event to process.</param>
    public void Emit(HoneEvent honeEvent);
}
