namespace Hone.Agents.Core;

/// <summary>
/// Result of an AI agent invocation with an optional strongly-typed parsed result.
/// </summary>
/// <typeparam name="T">The deserialized response type.</typeparam>
/// <remarks>
/// <para>When <see cref="Success"/> is <see langword="true"/>, <see cref="ParsedResult"/>
/// contains the deserialized agent response and <see cref="ResponseText"/> holds the
/// extracted JSON text.</para>
/// <para>When <see cref="Success"/> is <see langword="false"/>, <see cref="ParsedResult"/>
/// is <see langword="default"/> and <see cref="ResponseText"/> is empty.</para>
/// </remarks>
/// <param name="Success">Whether the agent invocation succeeded and produced a parseable response.</param>
/// <param name="ParsedResult">The deserialized response, or <see langword="default"/> on failure.</param>
/// <param name="RawOutput">Raw output from the agent process. Never null; may be empty on exception.</param>
/// <param name="ResponseText">Extracted JSON response text. Never null; empty when parsing fails or the agent errors.</param>
/// <param name="TimedOut">Whether the agent invocation exceeded the configured timeout.</param>
/// <param name="ExitCode">Process exit code. <c>-1</c> when the process could not be started.</param>
public sealed record AgentResult<T>(
    bool Success,
    T? ParsedResult,
    string RawOutput,
    string ResponseText,
    bool TimedOut,
    int ExitCode);
