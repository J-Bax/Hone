using Hone.Core.Models;

namespace Hone.Core.Contracts;

/// <summary>
/// Generic process execution abstraction.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs an external process and returns the result.
    /// </summary>
    public Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default);
}
