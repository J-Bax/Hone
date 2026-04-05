using FluentAssertions;
using Hone.Core.Contracts;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Contracts;

public sealed class PlaceholderInterfaceTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void ICollectorPlugin_Interface_Exists()
    {
        System.Type iface = typeof(ICollectorPlugin);

        _ = iface.Should().NotBeNull();
        _ = iface.IsInterface.Should().BeTrue();
    }

    [Fact]
    public void IAnalyzerPlugin_Interface_Exists()
    {
        System.Type iface = typeof(IAnalyzerPlugin);

        _ = iface.Should().NotBeNull();
        _ = iface.IsInterface.Should().BeTrue();
    }

    [Fact]
    public void ILifecycleHook_Interface_Exists()
    {
        System.Type iface = typeof(ILifecycleHook);

        _ = iface.Should().NotBeNull();
        _ = iface.IsInterface.Should().BeTrue();
    }
}
