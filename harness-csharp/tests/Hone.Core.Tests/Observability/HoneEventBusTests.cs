using FluentAssertions;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.TestInfrastructure;
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
}
