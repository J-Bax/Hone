namespace Hone.Lifecycle.Hooks;

/// <summary>
/// Configuration loaded from the target project's .hone/config.yaml.
/// </summary>
public sealed record TargetConfig(
    string Name = "",
    string BaseBranch = "main",
    IReadOnlyDictionary<string, TargetHookConfig>? Hooks = null)
{
    /// <summary>Gets the hook definitions.</summary>
    public IReadOnlyDictionary<string, TargetHookConfig> Hooks { get; init; } =
        Hooks ?? new Dictionary<string, TargetHookConfig>(StringComparer.OrdinalIgnoreCase);
}
