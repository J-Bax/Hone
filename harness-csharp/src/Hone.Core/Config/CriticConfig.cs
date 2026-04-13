namespace Hone.Core.Config;

/// <summary>
/// Configuration for the critic review gate.
/// </summary>
public sealed record CriticConfig(
    bool Enabled = false);
