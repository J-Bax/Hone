namespace Hone.Core.Models;

/// <summary>
/// Result of running an external process.
/// </summary>
public sealed record ProcessResult(
    bool Success,
    string Output,
    int ExitCode,
    bool TimedOut);
