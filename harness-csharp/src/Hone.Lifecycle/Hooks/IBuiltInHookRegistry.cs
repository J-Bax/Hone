using Hone.Core.Contracts;

namespace Hone.Lifecycle.Hooks;

/// <summary>
/// Registry that maps hook names to built-in C# hook implementations.
/// </summary>
public interface IBuiltInHookRegistry
{
    /// <summary>
    /// Gets the built-in hook implementation for the given hook name, or null if not registered.
    /// </summary>
    public ILifecycleHook? GetHook(string hookName);
}
