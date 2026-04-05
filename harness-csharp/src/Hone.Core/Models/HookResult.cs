using System.Text.Json.Serialization;

namespace Hone.Core.Models;

/// <summary>
/// Result of invoking a lifecycle hook.
/// </summary>
public sealed record HookResult(
    bool Success,
    string? Message,
    TimeSpan Duration,
    IReadOnlyList<string> Artifacts,
    Uri? BaseUrl,
    [property: JsonIgnore] object? Process)
{
    /// <summary>
    /// Gets the artifacts produced by the hook, defaulting to an empty list.
    /// </summary>
    public IReadOnlyList<string> Artifacts { get; init; } = Artifacts ?? [];
}
