namespace Hone.Core.Contracts;

/// <summary>
/// Result of an AI agent invocation.
/// </summary>
public sealed record AgentRunResult(
    bool Success,
    string Output,
    bool TimedOut,
    int ExitCode);
