using Hone.Core.Models;

namespace Hone.Core.Contracts;

/// <summary>
/// Contract for analysis plugins that process collected diagnostic data.
/// </summary>
public interface IAnalyzerPlugin
{
    /// <summary>
    /// Gets the display name of this analyzer.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the names of collectors whose data this analyzer requires.
    /// </summary>
    public IReadOnlyList<string> RequiredCollectors { get; }

    /// <summary>
    /// Analyzes collected diagnostic data and produces a report.
    /// </summary>
    public Task<AnalyzerResult> AnalyzeAsync(
        AnalyzerContext context,
        CancellationToken ct = default);
}
