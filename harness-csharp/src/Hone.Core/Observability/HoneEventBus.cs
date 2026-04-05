namespace Hone.Core.Observability;

/// <summary>
/// Broadcasts <see cref="HoneEvent"/> instances to all registered
/// <see cref="IHoneEventSink"/> implementations. Thread-safe.
/// </summary>
public sealed class HoneEventBus : IHoneEventSink
{
    private readonly List<IHoneEventSink> _sinks = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Registers a sink to receive all future events.
    /// </summary>
    /// <param name="sink">The sink to register.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    public void Register(IHoneEventSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        lock (_lock)
        {
            _sinks.Add(sink);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// If a sink throws during emission, the exception is caught and remaining sinks
    /// still receive the event. This ensures one faulty sink cannot break observability.
    /// </remarks>
    public void Emit(HoneEvent honeEvent)
    {
        ArgumentNullException.ThrowIfNull(honeEvent);

        IHoneEventSink[] snapshot;
        lock (_lock)
        {
            snapshot = [.. _sinks];
        }

        foreach (IHoneEventSink sink in snapshot)
        {
            try
            {
                sink.Emit(honeEvent);
            }
#pragma warning disable CA1031 // Resilience: one bad sink must not break broadcasting
            catch
#pragma warning restore CA1031
            {
                // Swallowed  observability must not crash the harness.
            }
        }
    }
}