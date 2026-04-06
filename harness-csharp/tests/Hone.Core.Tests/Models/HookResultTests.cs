using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Xunit;

namespace Hone.Core.Tests.Models;

public sealed class HookResultTests
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        HookResult original = new(
            Success: true,
            Message: "Hook completed",
            Duration: TimeSpan.FromSeconds(5.5),
            Artifacts: ["report.json", "trace.nettrace"],
            BaseUrl: new Uri("http://localhost:8080"),
            Process: null);

        string json = JsonSerializer.Serialize(original);
        HookResult? deserialized = JsonSerializer.Deserialize<HookResult>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Success.Should().Be(original.Success);
        _ = deserialized.Message.Should().Be(original.Message);
        _ = deserialized.Duration.Should().Be(original.Duration);
        _ = deserialized.Artifacts.Should().BeEquivalentTo(original.Artifacts);
        _ = deserialized.BaseUrl.Should().Be(original.BaseUrl);
    }

    [Fact]
    public void RoundTrips_WithNullableFields()
    {
        HookResult original = new(
            Success: false,
            Message: null,
            Duration: TimeSpan.Zero,
            Artifacts: [],
            BaseUrl: null,
            Process: null);

        string json = JsonSerializer.Serialize(original);
        HookResult? deserialized = JsonSerializer.Deserialize<HookResult>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Success.Should().BeFalse();
        _ = deserialized.Message.Should().BeNull();
        _ = deserialized.Duration.Should().Be(TimeSpan.Zero);
        _ = deserialized.Artifacts.Should().BeEmpty();
        _ = deserialized.BaseUrl.Should().BeNull();
    }

    [Fact]
    public void Process_IsExcludedFromJson()
    {
        HookResult original = new(
            Success: true,
            Message: null,
            Duration: TimeSpan.Zero,
            Artifacts: [],
            BaseUrl: null,
            Process: new object());

        string json = JsonSerializer.Serialize(original);
        _ = json.Should().NotContain("Process");
    }
}
