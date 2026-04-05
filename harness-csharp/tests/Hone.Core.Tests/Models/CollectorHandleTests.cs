using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Models;

public sealed class CollectorHandleTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        CollectorHandle original = new(Success: true, Handle: null);

        string json = JsonSerializer.Serialize(original);
        CollectorHandle? deserialized = JsonSerializer.Deserialize<CollectorHandle>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Success.Should().BeTrue();
    }

    [Fact]
    public void Handle_IsExcludedFromJson()
    {
        CollectorHandle original = new(Success: true, Handle: new object());

        string json = JsonSerializer.Serialize(original);
        _ = json.Should().NotContain("Handle");
    }
}
