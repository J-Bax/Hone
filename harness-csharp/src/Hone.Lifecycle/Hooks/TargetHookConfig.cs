namespace Hone.Lifecycle.Hooks;

/// <summary>
/// Configuration for a single hook in the target's .hone/config.yaml.
/// </summary>
public sealed record TargetHookConfig(
    string Type,
    string? Name = null,
    string? Path = null,
    string? Value = null,
    string? Method = null);
