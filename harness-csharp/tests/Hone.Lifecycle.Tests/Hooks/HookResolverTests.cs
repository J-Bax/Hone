using FluentAssertions;
using Hone.Lifecycle.Hooks;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Lifecycle.Tests.Hooks;

public sealed class HookResolverTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void Resolve_BuiltInHook_ResolvesToNativeImplementation()
    {
        TargetConfig config = ConfigWith("build", new TargetHookConfig(Type: "BuiltIn", Name: "dotnet-build"));

        ResolvedHook result = HookResolver.Resolve("build", config);

        _ = result.Type.Should().Be(HookType.BuiltIn);
        _ = result.BuiltInName.Should().Be("dotnet-build");
        _ = result.Command.Should().BeNull();
        _ = result.Url.Should().BeNull();
        _ = result.HttpMethod.Should().BeNull();
    }

    [Fact]
    public void Resolve_CommandHook_PassesThrough()
    {
        TargetConfig config = ConfigWith("build", new TargetHookConfig(Type: "Command", Value: "dotnet build"));

        ResolvedHook result = HookResolver.Resolve("build", config);

        _ = result.Type.Should().Be(HookType.Command);
        _ = result.Command.Should().Be("dotnet build");
    }

    [Fact]
    public void Resolve_HttpHook_PassesThrough()
    {
        TargetConfig config = ConfigWith("start", new TargetHookConfig(Type: "Http", Path: "/api/start", Method: "POST"));

        ResolvedHook result = HookResolver.Resolve("start", config);

        _ = result.Type.Should().Be(HookType.Http);
        _ = result.Url.Should().Be(new Uri("/api/start", UriKind.RelativeOrAbsolute));
        _ = result.HttpMethod.Should().Be("POST");
    }

    [Fact]
    public void Resolve_HttpHook_DefaultsToGet()
    {
        TargetConfig config = ConfigWith("health", new TargetHookConfig(Type: "Http", Path: "/health"));

        ResolvedHook result = HookResolver.Resolve("health", config);

        _ = result.Type.Should().Be(HookType.Http);
        _ = result.HttpMethod.Should().Be("GET");
    }

    [Fact]
    public void Resolve_SkipHook_PassesThrough()
    {
        TargetConfig config = ConfigWith("teardown", new TargetHookConfig(Type: "Skip"));

        ResolvedHook result = HookResolver.Resolve("teardown", config);

        _ = result.Type.Should().Be(HookType.Skip);
        _ = result.Command.Should().BeNull();
        _ = result.Url.Should().BeNull();
        _ = result.HttpMethod.Should().BeNull();
    }

    [Fact]
    public void Resolve_UndeclaredHook_ThrowsContractError()
    {
        TargetConfig config = new();

        Action act = () => HookResolver.Resolve("missing-hook", config);

        _ = act.Should().Throw<InvalidOperationException>()
            .WithMessage("*.hone/config.yaml must declare Hooks.missing-hook*");
    }

    [Fact]
    public void Resolve_UnknownType_Throws()
    {
        TargetConfig config = ConfigWith("build", new TargetHookConfig(Type: "FooBar"));

        Action act = () => HookResolver.Resolve("build", config);

        _ = act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Unknown hook type 'FooBar'*");
    }

    [Fact]
    public void Resolve_CommandHook_MissingValue_Throws()
    {
        TargetConfig config = ConfigWith("build", new TargetHookConfig(Type: "Command"));

        Action act = () => HookResolver.Resolve("build", config);

        _ = act.Should().Throw<InvalidOperationException>()
            .WithMessage("*missing Value for Command hook*");
    }

    [Fact]
    public void Resolve_HttpHook_MissingPath_Throws()
    {
        TargetConfig config = ConfigWith("start", new TargetHookConfig(Type: "Http"));

        Action act = () => HookResolver.Resolve("start", config);

        _ = act.Should().Throw<InvalidOperationException>()
            .WithMessage("*missing Path for Http hook*");
    }

    [Fact]
    public void Resolve_CaseInsensitive_TypeMatching()
    {
        TargetConfig config = ConfigWith("build", new TargetHookConfig(Type: "builtin", Name: "dotnet-build"));

        ResolvedHook result = HookResolver.Resolve("build", config);

        _ = result.Type.Should().Be(HookType.BuiltIn);
    }

    [Fact]
    public void Resolve_BuiltInHook_MissingName_Throws()
    {
        TargetConfig config = ConfigWith("build", new TargetHookConfig(Type: "BuiltIn"));

        Action act = () => HookResolver.Resolve("build", config);

        _ = act.Should().Throw<InvalidOperationException>()
            .WithMessage("*missing Name for BuiltIn hook*");
    }

    private static TargetConfig ConfigWith(string hookName, TargetHookConfig hookConfig)
    {
        Dictionary<string, TargetHookConfig> hooks = new(StringComparer.OrdinalIgnoreCase)
        {
            [hookName] = hookConfig,
        };
        return new TargetConfig(Hooks: hooks);
    }
}
