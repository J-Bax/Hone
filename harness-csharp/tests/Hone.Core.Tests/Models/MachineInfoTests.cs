using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Models;

public sealed class MachineInfoTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        MachineInfo original = new(
            CpuName: "Intel Core i9-13900K",
            CpuCores: 24,
            TotalRamGB: 64.0m,
            OsVersion: "Windows 11 23H2",
            DotnetVersion: "10.0.0");

        string json = JsonSerializer.Serialize(original);
        MachineInfo? deserialized = JsonSerializer.Deserialize<MachineInfo>(json);

        _ = deserialized.Should().Be(original);
    }

    [Fact]
    public void RoundTrips_WithAllNullFields()
    {
        MachineInfo original = new(
            CpuName: null,
            CpuCores: null,
            TotalRamGB: null,
            OsVersion: null,
            DotnetVersion: null);

        string json = JsonSerializer.Serialize(original);
        MachineInfo? deserialized = JsonSerializer.Deserialize<MachineInfo>(json);

        _ = deserialized.Should().Be(original);
    }

    [Fact]
    public void RoundTrips_WithPartialNullFields()
    {
        MachineInfo original = new(
            CpuName: "Apple M2 Pro",
            CpuCores: 12,
            TotalRamGB: null,
            OsVersion: null,
            DotnetVersion: "10.0.0");

        string json = JsonSerializer.Serialize(original);
        MachineInfo? deserialized = JsonSerializer.Deserialize<MachineInfo>(json);

        _ = deserialized.Should().Be(original);
    }
}
