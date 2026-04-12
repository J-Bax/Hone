using FluentAssertions;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.TestInfrastructure;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Observability;

public sealed class HoneEventBusTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void EventBus_BroadcastsToAllSinks()
    {
        var bus = new HoneEventBus();
        var sink1 = new RecordingSink();
        var sink2 = new RecordingSink();
        var sink3 = new RecordingSink();

        bus.Register(sink1);
        bus.Register(sink2);
        bus.Register(sink3);

        var evt = new StatusMessage(
            "Test message", LogLevel.Info, DateTimeOffset.UtcNow, Experiment: 1);

        bus.Emit(evt);

        _ = sink1.Events.Should().ContainSingle().Which.Should().Be(evt);
        _ = sink2.Events.Should().ContainSingle().Which.Should().Be(evt);
        _ = sink3.Events.Should().ContainSingle().Which.Should().Be(evt);
    }

    [Fact]
    public void EventBus_ContinuesBroadcasting_WhenSinkThrows()
    {
        var bus = new HoneEventBus();
        var sink1 = new RecordingSink();
        var throwingSink = new ThrowingSink();
        var sink3 = new RecordingSink();

        bus.Register(sink1);
        bus.Register(throwingSink);
        bus.Register(sink3);

        var evt = new StatusMessage(
            "Test message", LogLevel.Info, DateTimeOffset.UtcNow, Experiment: 1);

        bus.Emit(evt);

        _ = sink1.Events.Should().ContainSingle();
        _ = sink3.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task EventBus_SerializesSharedConsoleSinkWrites_DuringConcurrentEmission()
    {
        var bus = new HoneEventBus();
        var writer = new DetectConcurrentWriteTextWriter();
        var sink = new ConsoleEventSink(writer);

        bus.Register(sink);

        using var start = new ManualResetEventSlim(initialState: false);
        Task[] tasks = [.. Enumerable.Range(0, 32)
            .Select(i => Task.Run(() =>
            {
                start.Wait();
                bus.Emit(new StatusMessage(
                    $"message-{i}", LogLevel.Info, DateTimeOffset.UtcNow, Experiment: i));
            })),];

        start.Set();
        await Task.WhenAll(tasks);

        _ = writer.ConcurrentWriteDetected.Should().BeFalse();
        _ = writer.LineCount.Should().Be(32);
    }

    private sealed class RecordingSink : IHoneEventSink
    {
        public List<HoneEvent> Events { get; } = [];

        public void Emit(HoneEvent @event)
        {
            Events.Add(@event);
        }
    }

    private sealed class ThrowingSink : IHoneEventSink
    {
        public void Emit(HoneEvent @event)
        {
            throw new InvalidOperationException("Sink failure");
        }
    }

    private sealed class DetectConcurrentWriteTextWriter : TextWriter
    {
        private int _activeWriters;

        public override Encoding Encoding => Encoding.UTF8;

        public bool ConcurrentWriteDetected { get; private set; }

        public int LineCount { get; private set; }

        public override void WriteLine(string? value)
        {
            if (Interlocked.Increment(ref _activeWriters) != 1)
            {
                ConcurrentWriteDetected = true;
            }

            try
            {
                _ = SpinWait.SpinUntil(static () => false, millisecondsTimeout: 10);
                LineCount++;
            }
            finally
            {
                _ = Interlocked.Decrement(ref _activeWriters);
            }
        }
    }
}
