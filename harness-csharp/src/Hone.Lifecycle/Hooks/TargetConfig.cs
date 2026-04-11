using Hone.Core.Config;

namespace Hone.Lifecycle.Hooks;

/// <summary>
/// Configuration loaded from the target project's .hone/config.yaml.
/// </summary>
/// <remarks>
/// Targets may override engine-level diagnostic settings to disable tools that
/// don't apply to their runtime (e.g., PerfView is Windows-only, dotnet-counters
/// requires the .NET runtime). Any <see cref="DiagnosticsConfig"/> properties set
/// here take precedence over the engine defaults through
/// <see cref="ConfigMerger.Merge"/>.
/// </remarks>
public sealed record TargetConfig(
    string Name = "",
    string BaseBranch = "main",
    IReadOnlyDictionary<string, TargetHookConfig>? Hooks = null,
    DiagnosticsConfig? Diagnostics = null)
{
    /// <summary>Gets the hook definitions.</summary>
    public IReadOnlyDictionary<string, TargetHookConfig> Hooks { get; init; } =
        Hooks ?? new Dictionary<string, TargetHookConfig>(StringComparer.OrdinalIgnoreCase);
}
