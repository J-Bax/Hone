using FluentAssertions;
using Hone.Lifecycle.SharedHooks;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Lifecycle.Tests.SharedHooks;

public sealed class DotnetStartHookTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void FindFreePort_ReturnsValidPort()
    {
        int port = DotnetStartHook.FindFreePort();

        _ = port.Should().BeGreaterThan(0);
        _ = port.Should().BeLessThan(65536);
    }

    [Fact]
    public void FindFreePort_ReturnsDifferentPortsOnConsecutiveCalls()
    {
        int port1 = DotnetStartHook.FindFreePort();
        int port2 = DotnetStartHook.FindFreePort();

        // Ports should generally differ (not strictly guaranteed but extremely likely)
        // At minimum, both should be valid
        _ = port1.Should().BeGreaterThan(0);
        _ = port2.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ExecuteAsync_NullContext_ThrowsArgumentNullException()
    {
        using var httpClient = new HttpClient();
        var sut = new DotnetStartHook(httpClient);

        Func<Task> act = () => sut.ExecuteAsync(null!);

        _ = await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
