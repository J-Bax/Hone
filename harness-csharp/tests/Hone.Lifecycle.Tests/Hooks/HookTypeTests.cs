using FluentAssertions;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Lifecycle.Hooks;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Lifecycle.Tests.Hooks;

public sealed class HookTypeTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void HookType_HasExactlyFourValues()
    {
        HookType[] values = Enum.GetValues<HookType>();
        _ = values.Should().HaveCount(4);
    }

    [Theory]
    [InlineData(HookType.BuiltIn, 0)]
    [InlineData(HookType.Command, 1)]
    [InlineData(HookType.Http, 2)]
    [InlineData(HookType.Skip, 3)]
    public void HookType_ValuesAreCorrect(HookType type, int expectedValue)
    {
        _ = ((int)type).Should().Be(expectedValue);
    }

    [Fact]
    public void ResolvedHook_BuiltIn_ProducesCorrectType()
    {
        var hook = ResolvedHook.BuiltIn();

        _ = hook.Type.Should().Be(HookType.BuiltIn);
        _ = hook.Command.Should().BeNull();
        _ = hook.Url.Should().BeNull();
        _ = hook.HttpMethod.Should().BeNull();
    }

    [Fact]
    public void ResolvedHook_ForCommand_ProducesCorrectType()
    {
        var hook = ResolvedHook.ForCommand("dotnet build");

        _ = hook.Type.Should().Be(HookType.Command);
        _ = hook.Command.Should().Be("dotnet build");
        _ = hook.Url.Should().BeNull();
        _ = hook.HttpMethod.Should().BeNull();
    }

    [Fact]
    public void ResolvedHook_ForHttp_ProducesCorrectType_DefaultMethod()
    {
        var hook = ResolvedHook.ForHttp(new Uri("https://localhost:5000/health"));

        _ = hook.Type.Should().Be(HookType.Http);
        _ = hook.Url.Should().Be(new Uri("https://localhost:5000/health"));
        _ = hook.HttpMethod.Should().Be("GET");
        _ = hook.Command.Should().BeNull();
    }

    [Fact]
    public void ResolvedHook_ForHttp_ProducesCorrectType_CustomMethod()
    {
        var hook = ResolvedHook.ForHttp(new Uri("https://localhost:5000/start"), "POST");

        _ = hook.Type.Should().Be(HookType.Http);
        _ = hook.Url.Should().Be(new Uri("https://localhost:5000/start"));
        _ = hook.HttpMethod.Should().Be("POST");
    }

    [Fact]
    public void ResolvedHook_Skipped_ProducesCorrectType()
    {
        var hook = ResolvedHook.Skipped();

        _ = hook.Type.Should().Be(HookType.Skip);
        _ = hook.Command.Should().BeNull();
        _ = hook.Url.Should().BeNull();
        _ = hook.HttpMethod.Should().BeNull();
    }

    [Fact]
    public void HookContext_IsConstructibleWithAllProperties()
    {
        HoneConfig config = new();
        HookContext context = new(
            TargetPath: @"C:\targets\myapp",
            Config: config,
            BaseUrl: new Uri("https://localhost:5000"),
            Experiment: 1);

        _ = context.TargetPath.Should().Be(@"C:\targets\myapp");
        _ = context.Config.Should().BeSameAs(config);
        _ = context.BaseUrl.Should().Be(new Uri("https://localhost:5000"));
        _ = context.Experiment.Should().Be(1);
    }

    [Fact]
    public void HookContext_AllowsNullBaseUrl()
    {
        HookContext context = new(
            TargetPath: "/targets/myapp",
            Config: new HoneConfig(),
            BaseUrl: null,
            Experiment: 0);

        _ = context.BaseUrl.Should().BeNull();
    }
}
