namespace Hone.Core.Config;

/// <summary>
/// Configuration for AI agent models and timeouts.
/// </summary>
public sealed record AgentConfig(
    string DefaultModel = "claude-sonnet-4.5",
    string? AnalysisModel = "claude-opus-4.6",
    string? ClassificationModel = "claude-opus-4.6",
    string? ImplementerModel = "claude-sonnet-4.6",
    string? CriticModel = null,
    int AgentTimeoutSec = 1800);
