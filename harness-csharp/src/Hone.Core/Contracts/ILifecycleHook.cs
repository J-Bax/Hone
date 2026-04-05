using Hone.Core.Models;

namespace Hone.Core.Contracts;

/// <summary>
/// Contract for experiment lifecycle hooks (e.g., build, start, stop, test).
/// Each built-in hook implements this interface.
/// </summary>
public interface ILifecycleHook
{
    /// <summary>
    /// Executes the hook and returns the result.
    /// </summary>
    public Task<HookResult> ExecuteAsync(HookContext context, CancellationToken ct = default);
}
