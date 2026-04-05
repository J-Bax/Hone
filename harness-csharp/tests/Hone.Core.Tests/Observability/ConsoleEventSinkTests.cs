using FluentAssertions;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Observability;

public sealed class ConsoleEventSinkTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(2024, 1, 15, 14, 30, 45, TimeSpan.Zero);

    [Fact]
    public void ConsoleEventSink_Timestamps_NonBoxDrawing()
    {
        var writer = new StringWriter();
        var sink = new ConsoleEventSink(writer);

        sink.Emit(new StatusMessage(
            "Starting build", LogLevel.Info, FixedTimestamp, Experiment: 1));

        string result = writer.ToString().TrimEnd();
        _ = result.Should().Be("[14:30:45] Starting build");
    }

    [Fact]
    public void ConsoleEventSink_PassesThrough_BoxDrawing()
    {
        var writer = new StringWriter();
        var sink = new ConsoleEventSink(writer);

        sink.Emit(new StatusMessage(
            "═══════════", LogLevel.Info, FixedTimestamp, Experiment: 1));

        string result = writer.ToString().TrimEnd();
        _ = result.Should().Be("═══════════");
    }

    [Theory]
    [InlineData("━━━━━━━━")]
    [InlineData("═══════")]
    [InlineData("───────")]
    [InlineData("╔═══════╗")]
    [InlineData("╚═══════╝")]
    [InlineData("║ content")]
    [InlineData("╠═══════╣")]
    [InlineData("╦═══════")]
    [InlineData("╩═══════")]
    public void ConsoleEventSink_PassesThrough_AllBoxDrawingChars(string message)
    {
        var writer = new StringWriter();
        var sink = new ConsoleEventSink(writer);

        sink.Emit(new StatusMessage(
            message, LogLevel.Info, FixedTimestamp, Experiment: 1));

        string result = writer.ToString().TrimEnd();
        _ = result.Should().Be(message);
    }

    [Fact]
    public void ConsoleEventSink_PassesThrough_BlankMessage()
    {
        var writer = new StringWriter();
        var sink = new ConsoleEventSink(writer);

        sink.Emit(new StatusMessage(
            "   ", LogLevel.Info, FixedTimestamp, Experiment: 1));

        string result = writer.ToString().TrimEnd();
        _ = result.Should().BeEmpty();
    }

    [Fact]
    public void ConsoleEventSink_PassesThrough_EmptyMessage()
    {
        var writer = new StringWriter();
        var sink = new ConsoleEventSink(writer);

        sink.Emit(new StatusMessage(
            "", LogLevel.Info, FixedTimestamp, Experiment: 1));

        string result = writer.ToString().TrimEnd();
        _ = result.Should().BeEmpty();
    }

    [Fact]
    public void ConsoleEventSink_Timestamps_NonStatusEvents()
    {
        var writer = new StringWriter();
        var sink = new ConsoleEventSink(writer);

        sink.Emit(new PhaseStarted("Build", FixedTimestamp, Experiment: 1));

        string result = writer.ToString().TrimEnd();
        _ = result.Should().StartWith("[14:30:45]");
    }
}
