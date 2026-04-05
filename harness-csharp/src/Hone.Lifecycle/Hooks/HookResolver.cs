namespace Hone.Lifecycle.Hooks;

/// <summary>
/// Resolves a hook definition from target configuration into an executable descriptor.
/// Replaces the PowerShell <c>Resolve-Hook</c> function in HoneHelpers.psm1.
/// </summary>
public static class HookResolver
{
    /// <summary>
    /// Resolves the named hook from the target configuration.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when hook is undeclared or has unknown type.</exception>
    public static ResolvedHook Resolve(string hookName, TargetConfig targetConfig)
    {
        ArgumentNullException.ThrowIfNull(targetConfig);

        if (!targetConfig.Hooks.TryGetValue(hookName, out TargetHookConfig? hookDef))
        {
            throw new InvalidOperationException(
                $".hone/config.yaml must declare Hooks.{hookName} (use Type = 'Skip' if not needed)");
        }

        return hookDef.Type.ToUpperInvariant() switch
        {
            "BUILTIN" => ResolvedHook.BuiltIn(),
            "COMMAND" => ResolvedHook.ForCommand(
                hookDef.Value ?? throw new InvalidOperationException(
                    $"Hooks.{hookName} is missing Value for Command hook")),
            "HTTP" => ResolvedHook.ForHttp(
                new Uri(hookDef.Path ?? throw new InvalidOperationException(
                    $"Hooks.{hookName} is missing Path for Http hook"), UriKind.RelativeOrAbsolute),
                hookDef.Method),
            "SKIP" => ResolvedHook.Skipped(),
            _ => throw new InvalidOperationException(
                $"Unknown hook type '{hookDef.Type}' for Hooks.{hookName}"),
        };
    }
}
