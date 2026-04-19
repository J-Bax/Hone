namespace Hone.Orchestration.State;

/// <summary>
/// Persists the durable control-plane documents rooted under the metadata directory.
/// </summary>
internal interface IRunStateStore
{
    /// <summary>Gets the absolute metadata directory on disk.</summary>
    internal string MetadataDirectory { get; }

    /// <summary>Gets the absolute path to <c>run-state.json</c>.</summary>
    internal string RunStatePath { get; }

    /// <summary>
    /// Gets the configured cleanup-manifest path for the given experiment.
    /// Returned paths are rooted at the configured metadata path.
    /// </summary>
    internal string GetCleanupManifestPath(int experiment);

    /// <summary>Loads the current run-state document, or <c>null</c> when it does not exist.</summary>
    internal Task<RunStateDocument?> LoadAsync(CancellationToken ct = default);

    /// <summary>Saves the current run-state document atomically.</summary>
    internal Task SaveAsync(RunStateDocument document, CancellationToken ct = default);

    /// <summary>
    /// Loads a cleanup manifest from an absolute or target-relative path rooted under metadata.
    /// </summary>
    internal Task<CleanupManifest?> LoadCleanupManifestAsync(string manifestPath, CancellationToken ct = default);

    /// <summary>
    /// Saves a cleanup manifest to an absolute or target-relative path rooted under metadata.
    /// </summary>
    internal Task SaveCleanupManifestAsync(string manifestPath, CleanupManifest manifest, CancellationToken ct = default);
}
