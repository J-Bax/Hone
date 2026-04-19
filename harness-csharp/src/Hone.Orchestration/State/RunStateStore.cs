using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hone.Orchestration.State;

/// <summary>
/// Persists <c>run-state.json</c> and related cleanup manifests under the metadata directory.
/// </summary>
internal sealed class RunStateStore : IRunStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _targetDir;
    private readonly string _metadataPath;

    internal RunStateStore(string targetDir, string metadataPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetDir);
        ArgumentException.ThrowIfNullOrEmpty(metadataPath);

        _targetDir = Path.GetFullPath(targetDir);
        _metadataPath = metadataPath;
        MetadataDirectory = ResolvePath(metadataPath);
        RunStatePath = Path.Combine(MetadataDirectory, "run-state.json");
    }

    /// <inheritdoc />
    public string MetadataDirectory { get; }

    /// <inheritdoc />
    public string RunStatePath { get; }

    /// <inheritdoc />
    public string GetCleanupManifestPath(int experiment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(experiment);
        return Path.Combine(_metadataPath, "cleanup", $"experiment-{experiment}.json");
    }

    /// <inheritdoc />
    public async Task<RunStateDocument?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(RunStatePath))
        {
            return null;
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(RunStatePath, ct).ConfigureAwait(false);
            RunStateDocument? document = JsonSerializer.Deserialize<RunStateDocument>(bytes, SerializerOptions);
            if (document is null)
            {
                return null;
            }

            EnsureSupportedSchema(
                document.SchemaVersion,
                RunStateDocument.CurrentSchemaVersion,
                RunStatePath);

            return document;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse run state document at '{RunStatePath}': {ex.Message}",
                ex);
        }
    }

    /// <inheritdoc />
    public Task SaveAsync(RunStateDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        EnsureSupportedSchema(
            document.SchemaVersion,
            RunStateDocument.CurrentSchemaVersion,
            RunStatePath);

        return WriteAsync(RunStatePath, document, ct);
    }

    /// <inheritdoc />
    public async Task<CleanupManifest?> LoadCleanupManifestAsync(string manifestPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(manifestPath);

        string fullPath = ResolvePath(manifestPath);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
            CleanupManifest? manifest = JsonSerializer.Deserialize<CleanupManifest>(bytes, SerializerOptions);
            if (manifest is null)
            {
                return null;
            }

            EnsureSupportedSchema(
                manifest.SchemaVersion,
                CleanupManifest.CurrentSchemaVersion,
                fullPath);

            return manifest;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse cleanup manifest at '{fullPath}': {ex.Message}",
                ex);
        }
    }

    /// <inheritdoc />
    public Task SaveCleanupManifestAsync(
        string manifestPath,
        CleanupManifest manifest,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(manifestPath);
        ArgumentNullException.ThrowIfNull(manifest);

        string fullPath = ResolvePath(manifestPath);
        EnsureSupportedSchema(
            manifest.SchemaVersion,
            CleanupManifest.CurrentSchemaVersion,
            fullPath);

        return WriteAsync(fullPath, manifest, ct);
    }

    private string ResolvePath(string path) =>
        Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(_targetDir, path));

    private static void EnsureSupportedSchema(int actualSchemaVersion, int expectedSchemaVersion, string path)
    {
        if (actualSchemaVersion != expectedSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported schema version '{actualSchemaVersion}' at '{path}'. Expected '{expectedSchemaVersion}'.");
        }
    }

    private static async Task WriteAsync<TDocument>(string path, TDocument document, CancellationToken ct)
        where TDocument : notnull
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = path + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions);
        await File.WriteAllBytesAsync(tempPath, bytes, ct).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }
}
