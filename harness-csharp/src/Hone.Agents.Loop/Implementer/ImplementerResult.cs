namespace Hone.Agents.Loop.Implementer;

/// <summary>
/// Result of a code implementation attempt by the hone-implementer agent.
/// </summary>
/// <remarks>
/// <para>When <see cref="Success"/> is <see langword="true"/>,
/// <see cref="CodeBlock"/> contains the extracted code from the agent response.</para>
/// <para>When <see cref="Success"/> is <see langword="false"/>,
/// <see cref="CodeBlock"/> is null (the agent did not produce a usable code block).</para>
/// </remarks>
public sealed record ImplementerResult(
    bool Success,
    string? CodeBlock,
    string Response,
    int Attempt);
