using Hone.Core.Models;

namespace Hone.Diagnostics.Discovery;

/// <summary>
/// A fully resolved collector ready to participate in a diagnostic run.
/// </summary>
public sealed record DiscoveredCollector(
    string Name,
    string Directory,
    CollectorMetadata Metadata,
    CollectorSettings MergedSettings,
    string Group);
