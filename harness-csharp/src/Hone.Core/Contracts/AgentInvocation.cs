namespace Hone.Core.Contracts;

/// <summary>
/// Parameters for invoking an AI agent.
/// </summary>
public sealed record AgentInvocation(
    string AgentName,
    string Prompt,
    string? Model = null,
    TimeSpan? Timeout = null,
    string? WorkingDirectory = null);
