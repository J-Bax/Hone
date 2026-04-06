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
    [property: JsonIgnore, Obsolete("Unused — will be removed")] object? Process = null)
{
    public IReadOnlyList<string> Artifacts { get; init; } = Artifacts;
}
