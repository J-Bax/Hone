namespace Hone.Agents.Preparation;

/// <summary>
/// Result returned by <see cref="CompatibilityAgent.AssessAsync"/>.
/// </summary>
public sealed record CompatibilityResult(
    bool Success,
    string Message,
    CompatibilityReport? Report);
