namespace Hone.Orchestration.Queue;

/// <summary>
/// Result of initializing the optimization queue from analysis output.
/// </summary>
internal sealed record InitializeResult(bool Success, int Count);
