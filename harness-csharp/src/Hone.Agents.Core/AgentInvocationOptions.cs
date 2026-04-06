namespace Hone.Agents.Core;

/// <summary>
/// Options for invoking an AI agent through <see cref="AgentInvoker"/>.
/// </summary>
public sealed record AgentInvocationOptions(
    string AgentName,
    string Prompt,
    string? ModelConfigKey = null,
    string? DefaultModel = null,
    int MaxRetries = 0,
    string? RetryPromptSuffix = null,
    string? WorkingDirectory = null);
