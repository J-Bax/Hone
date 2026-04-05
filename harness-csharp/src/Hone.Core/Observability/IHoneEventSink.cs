namespace Hone.Core.Observability;

/// <summary>
/// Receives and processes Hone lifecycle events.
/// </summary>
public interface IHoneEventSink
{
    /// <summary>
    /// Processes a single harness event.
    /// </summary>
    /// <param name="honeEvent">The event to process.</param>
    public void Emit(HoneEvent honeEvent);
}