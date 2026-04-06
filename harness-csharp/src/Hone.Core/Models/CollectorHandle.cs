using System.Text.Json.Serialization;

namespace Hone.Core.Models;

/// <summary>
/// Handle returned when starting a data collector.
/// </summary>
public sealed record CollectorHandle(
    bool Success,
    [property: JsonIgnore] object? Handle);
