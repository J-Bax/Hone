namespace Hone.Agents.Loop.Implementer;

/// <summary>
/// Result of a code implementation attempt by the hone-fixer agent.
/// </summary>
public sealed record ImplementerResult(
    bool Success,
    string? CodeBlock,
    string Response,
    int Attempt);
