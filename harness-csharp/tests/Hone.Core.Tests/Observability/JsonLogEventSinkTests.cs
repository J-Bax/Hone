using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Observability;

public sealed class JsonLogEventSinkTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(2024, 1, 15, 14, 30, 45, TimeSpan.Zero);

    [Fact]
    public void JsonLogEventSink_WritesValidJsonl()
    {
        string logPath = Path.Combine(TempDir, "test.jsonl");
        var sink = new JsonLogEventSink(logPath);

        sink.Emit(new StatusMessage("Hello", LogLevel.Info, FixedTimestamp, Experiment: 1));
        sink.Emit(new PhaseStarted("Build", FixedTimestamp, Experiment: 2));

        string[] lines = [.. File.ReadAllLines(logPath)
            .Where(l => !string.IsNullOrWhiteSpace(l)),];

        _ = lines.Should().HaveCount(2);

        foreach (string line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            _ = doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        }
    }

    [Fact]
    public void JsonLogEventSink_RotatesAtMaxSize()
    {
        string logPath = Path.Combine(TempDir, "test.jsonl");
        var sink = new JsonLogEventSink(logPath, maxFileSizeBytes: 100);

        // Write enough data to exceed 100 bytes and trigger rotation
        for (int i = 0; i < 5; i++)
        {
            sink.Emit(new StatusMessage(
                $"Message {i}", LogLevel.Info, FixedTimestamp, Experiment: i));
        }

        _ = File.Exists(logPath + ".1").Should().BeTrue("rotated file should exist");
        _ = File.Exists(logPath).Should().BeTrue("current log should continue");
    }

    [Fact]
    public void JsonLogEventSink_IncludesAllEventFields()
    {
        string logPath = Path.Combine(TempDir, "test.jsonl");
        var sink = new JsonLogEventSink(logPath);

        sink.Emit(new PhaseStarted("Build", FixedTimestamp, Experiment: 42));

        string json = File.ReadAllLines(logPath)
            .First(l => !string.IsNullOrWhiteSpace(l));

        _ = json.Should().Contain("\"Phase\"");
        _ = json.Should().Contain("\"Build\"");
        _ = json.Should().Contain("\"Experiment\"");
        _ = json.Should().Contain("42");
        _ = json.Should().Contain("\"Timestamp\"");
    }

    [Fact]
    public void JsonLogEventSink_RotationDeletesPreviousRotatedFile()
    {
        string logPath = Path.Combine(TempDir, "test.jsonl");
        string rotatedPath = logPath + ".1";
        var sink = new JsonLogEventSink(logPath, maxFileSizeBytes: 50);

        // Emit many times to trigger multiple rotations
        for (int i = 0; i < 10; i++)
        {
            sink.Emit(new StatusMessage(
                $"Message {i}", LogLevel.Info, FixedTimestamp, Experiment: i));
        }

        // After multiple rotations, only .1 should exist (not .2, .3, etc.)
        _ = File.Exists(rotatedPath).Should().BeTrue();
        _ = File.Exists(logPath + ".2").Should().BeFalse();
    }

    [Fact]
    public void JsonLogEventSink_SerializesEnumsAsStrings()
    {
        string logPath = Path.Combine(TempDir, "test.jsonl");
        var sink = new JsonLogEventSink(logPath);

        sink.Emit(new StatusMessage("Test", LogLevel.Warning, FixedTimestamp, Experiment: 1));

        string json = File.ReadAllLines(logPath)
            .First(l => !string.IsNullOrWhiteSpace(l));

        _ = json.Should().Contain("\"Warning\"");
    }
}
