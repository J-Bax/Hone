using System.Reflection;
using FluentAssertions;
using Hone.Core.Contracts;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Contracts;

public sealed class IVersionControlTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void AllOperations_Present()
    {
        System.Type iface = typeof(IVersionControl);

        _ = iface.GetMethod("GetCurrentBranchAsync").Should().NotBeNull();
        _ = iface.GetMethod("CheckoutAsync").Should().NotBeNull();
        _ = iface.GetMethod("CommitAsync").Should().NotBeNull();
        _ = iface.GetMethod("GetDiffAsync").Should().NotBeNull();
        _ = iface.GetMethod("RevertLastCommitAsync").Should().NotBeNull();
    }

    [Fact]
    public void GetCurrentBranchAsync_ReturnsString()
    {
        MethodInfo? method = typeof(IVersionControl).GetMethod("GetCurrentBranchAsync");

        _ = method!.ReturnType.Should().Be<Task<string>>();
    }

    [Fact]
    public void CheckoutAsync_ReturnsTask()
    {
        MethodInfo? method = typeof(IVersionControl).GetMethod("CheckoutAsync");

        _ = method!.ReturnType.Should().Be<Task>();
    }

    [Fact]
    public void CommitAsync_ReturnsTask()
    {
        MethodInfo? method = typeof(IVersionControl).GetMethod("CommitAsync");

        _ = method!.ReturnType.Should().Be<Task>();
    }

    [Fact]
    public void GetDiffAsync_ReturnsString()
    {
        MethodInfo? method = typeof(IVersionControl).GetMethod("GetDiffAsync");

        _ = method!.ReturnType.Should().Be<Task<string>>();
    }

    [Fact]
    public void RevertLastCommitAsync_ReturnsTask()
    {
        MethodInfo? method = typeof(IVersionControl).GetMethod("RevertLastCommitAsync");

        _ = method!.ReturnType.Should().Be<Task>();
    }
}
