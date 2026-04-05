namespace Hone.Core.Config;

/// <summary>
/// Configuration for AI agent models and timeouts.
/// Mapped from the PowerShell <c>Copilot</c> section; renamed to be tool-agnostic.
/// </summary>
public sealed record AgentConfig(
    string DefaultModel = "claude-sonnet-4.5",
    string? AnalysisModel = "claude-opus-4.6",
    string? ClassificationModel = "claude-opus-4.6",
    string? ImplementerModel = "claude-sonnet-4.6",
    int AgentTimeoutSec = 1800);
