namespace Hone.Agents.Core;

/// <summary>
/// Result of an AI agent invocation with an optional strongly-typed parsed result.
/// </summary>
/// <typeparam name="T">The deserialized response type.</typeparam>
public sealed record AgentResult<T>(
    bool Success,
    T? ParsedResult,
    string RawOutput,
    string ResponseText,
    bool TimedOut,
    int ExitCode);
