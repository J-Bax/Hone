using System.Collections.Frozen;
using Hone.Core.Contracts;
using Hone.Lifecycle.SharedHooks;

namespace Hone.Lifecycle.Hooks;

/// <summary>
/// Maps built-in hook names (from <c>.hone/config.yaml</c>) to their C# implementations.
/// </summary>
public sealed class BuiltInHookRegistry : IBuiltInHookRegistry
{
    private readonly FrozenDictionary<string, ILifecycleHook> _hooks;

    public BuiltInHookRegistry(
        DotnetBuildHook dotnetBuild,
        DotnetTestHook dotnetTest,
        DotnetStartHook dotnetStart,
        DotnetStopHook dotnetStop,
        HealthPollHook healthPoll,
        K6RunHook k6Run)
    {
        _hooks = new Dictionary<string, ILifecycleHook>(StringComparer.OrdinalIgnoreCase)
        {
            ["dotnet-build"] = dotnetBuild,
            ["dotnet-test"] = dotnetTest,
            ["dotnet-start"] = dotnetStart,
            ["dotnet-stop"] = dotnetStop,
            ["health-poll"] = healthPoll,
            ["k6-run"] = k6Run,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public ILifecycleHook? GetHook(string hookName) =>
        _hooks.GetValueOrDefault(hookName);
}
