using System.Text.Json.Serialization;

namespace Hone.Core.Contracts;

/// <summary>
/// Opaque handle returned when starting runtime metrics collection.
/// </summary>
public sealed record MetricsCollectionHandle(
    [property: JsonIgnore] object Handle);
