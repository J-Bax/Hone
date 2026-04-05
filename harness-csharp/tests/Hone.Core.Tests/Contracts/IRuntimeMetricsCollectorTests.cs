using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Hone.Core.Contracts;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Contracts;

public sealed class IRuntimeMetricsCollectorTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void StartAsync_IsPresent()
    {
        MethodInfo? method = typeof(IRuntimeMetricsCollector).GetMethod("StartAsync");

        _ = method.Should().NotBeNull("IRuntimeMetricsCollector must define StartAsync");
        _ = method!.ReturnType.Should().Be<Task<MetricsCollectionHandle>>();
    }

    [Fact]
    public void StopAndParseAsync_IsPresent()
    {
        MethodInfo? method = typeof(IRuntimeMetricsCollector).GetMethod("StopAndParseAsync");

        _ = method.Should().NotBeNull("IRuntimeMetricsCollector must define StopAndParseAsync");
        _ = method!.ReturnType.Should().Be<Task<RuntimeMetricsResult>>();
    }

    [Fact]
    public void RuntimeMetricsOptions_RoundTrips_ThroughJson()
    {
        RuntimeMetricsOptions original = new(
            Providers: ["System.Runtime", "Microsoft.AspNetCore.Hosting"],
            RefreshIntervalSeconds: 5);

        string json = JsonSerializer.Serialize(original);
        RuntimeMetricsOptions? deserialized = JsonSerializer.Deserialize<RuntimeMetricsOptions>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Providers.Should().BeEquivalentTo(original.Providers);
        _ = deserialized.RefreshIntervalSeconds.Should().Be(original.RefreshIntervalSeconds);
    }

    [Fact]
    public void MetricsCollectionHandle_Handle_IsExcludedFromJson()
    {
        MetricsCollectionHandle original = new(Handle: new object());

        string json = JsonSerializer.Serialize(original);
        _ = json.Should().NotContain("Handle");
    }

    [Fact]
    public void RuntimeMetricsResult_RoundTrips_ThroughJson()
    {
        RuntimeMetricsResult original = new(
            Success: true,
            Counters: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["cpu-usage"] = 45.2,
                ["working-set"] = 1024000,
            });

        string json = JsonSerializer.Serialize(original);
        RuntimeMetricsResult? deserialized = JsonSerializer.Deserialize<RuntimeMetricsResult>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Success.Should().BeTrue();
        _ = deserialized.Counters.Should().Contain(
            new KeyValuePair<string, double>("cpu-usage", 45.2));
    }

    [Fact]
    public void RuntimeMetricsResult_RoundTrips_WithNullCounters()
    {
        RuntimeMetricsResult original = new(
            Success: false,
            Counters: null);

        string json = JsonSerializer.Serialize(original);
        RuntimeMetricsResult? deserialized = JsonSerializer.Deserialize<RuntimeMetricsResult>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Success.Should().BeFalse();
        _ = deserialized.Counters.Should().BeNull();
    }
}
