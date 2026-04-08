using FluentAssertions;
using Hone.Core.Contracts;
using Hone.Lifecycle.Hooks;
using Hone.Lifecycle.SharedHooks;
using Hone.TestInfrastructure;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Lifecycle.Tests.Hooks;

public sealed class BuiltInHookRegistryTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private static BuiltInHookRegistry CreateSut()
    {
        IProcessRunner processRunner = Substitute.For<IProcessRunner>();
        ILoadTestRunner loadTestRunner = Substitute.For<ILoadTestRunner>();
        using var httpClient = new HttpClient();

        return new BuiltInHookRegistry(
            new DotnetBuildHook(processRunner),
            new DotnetTestHook(processRunner),
            new DotnetStartHook(httpClient),
            new DotnetStopHook(),
            new HealthPollHook(httpClient),
            new K6RunHook(loadTestRunner));
    }

    [Theory]
    [InlineData("dotnet-build")]
    [InlineData("dotnet-test")]
    [InlineData("dotnet-start")]
    [InlineData("dotnet-stop")]
    [InlineData("health-poll")]
    [InlineData("k6-run")]
    public void GetHook_RegisteredName_ReturnsHook(string hookName)
    {
        BuiltInHookRegistry sut = CreateSut();

        ILifecycleHook? hook = sut.GetHook(hookName);

        _ = hook.Should().NotBeNull();
    }

    [Theory]
    [InlineData("Dotnet-Build")]
    [InlineData("DOTNET-STOP")]
    [InlineData("Health-Poll")]
    [InlineData("K6-RUN")]
    public void GetHook_CaseInsensitive_ReturnsHook(string hookName)
    {
        BuiltInHookRegistry sut = CreateSut();

        ILifecycleHook? hook = sut.GetHook(hookName);

        _ = hook.Should().NotBeNull();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("dotnet-run")]
    public void GetHook_UnknownName_ReturnsNull(string hookName)
    {
        BuiltInHookRegistry sut = CreateSut();

        ILifecycleHook? hook = sut.GetHook(hookName);

        _ = hook.Should().BeNull();
    }

    [Fact]
    public void GetHook_DotnetBuild_ReturnsDotnetBuildHook()
    {
        BuiltInHookRegistry sut = CreateSut();

        ILifecycleHook? hook = sut.GetHook("dotnet-build");

        _ = hook.Should().BeOfType<DotnetBuildHook>();
    }

    [Fact]
    public void GetHook_DotnetStop_ReturnsDotnetStopHook()
    {
        BuiltInHookRegistry sut = CreateSut();

        ILifecycleHook? hook = sut.GetHook("dotnet-stop");

        _ = hook.Should().BeOfType<DotnetStopHook>();
    }

    [Fact]
    public void GetHook_DotnetStart_ReturnsDotnetStartHook()
    {
        BuiltInHookRegistry sut = CreateSut();

        ILifecycleHook? hook = sut.GetHook("dotnet-start");

        _ = hook.Should().BeOfType<DotnetStartHook>();
    }
}
