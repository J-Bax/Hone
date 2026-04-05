using Hone.Core.Config;
using Hone.Core.Models;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Hone.Diagnostics.Discovery;

/// <summary>
/// Scans plugin directories for collector.yaml / analyzer.yaml manifests,
/// merges per-plugin settings from <see cref="DiagnosticsConfig"/>, and
/// returns fully resolved <see cref="DiscoveredCollector"/> / <see cref="DiscoveredAnalyzer"/> instances.
/// </summary>
public class PluginDiscoveryService
{
    private static readonly IDeserializer YamlDeserializer =
        new DeserializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

    /// <summary>
    /// Discovers all enabled collectors under <paramref name="collectorsPath"/>.
    /// </summary>
#pragma warning disable CA1822 // Instance method for DI/testability
    public async Task<IReadOnlyList<DiscoveredCollector>> DiscoverCollectorsAsync(
        string collectorsPath,
        DiagnosticsConfig config,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!Directory.Exists(collectorsPath))
        {
            return [];
        }

        var results = new List<DiscoveredCollector>();

        foreach (string subDir in Directory.GetDirectories(collectorsPath))
        {
            string manifestPath = Path.Combine(subDir, "collector.yaml");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            string yaml = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
            CollectorYamlModel raw = YamlDeserializer.Deserialize<CollectorYamlModel>(yaml) ?? new CollectorYamlModel();

            string dirName = Path.GetFileName(subDir);
            string name = raw.Name ?? dirName;
            string group = raw.Group ?? "default";

            // Check enabled status from config
            if (config.CollectorSettings.TryGetValue(dirName, out CollectorSettingsEntry? entry) && !entry.Enabled)
            {
                continue;
            }

            var metadata = new CollectorMetadata(
                Name: name,
                Description: raw.Description,
                Group: group,
                RequiresAdmin: raw.RequiresAdmin,
                OverheadImpact: raw.OverheadImpact,
                DefaultSettings: raw.DefaultSettings ?? new Dictionary<string, object?>(StringComparer.Ordinal));

            CollectorSettings mergedSettings = MergeCollectorSettings(metadata.DefaultSettings, dirName, config);

            results.Add(new DiscoveredCollector(
                Name: name,
                Directory: subDir,
                Metadata: metadata,
                MergedSettings: mergedSettings,
                Group: group));
        }

        return results;
    }
#pragma warning restore CA1822

    /// <summary>
    /// Discovers all enabled analyzers under <paramref name="analyzersPath"/>.
    /// </summary>
#pragma warning disable CA1822 // Instance method for DI/testability
    public async Task<IReadOnlyList<DiscoveredAnalyzer>> DiscoverAnalyzersAsync(
        string analyzersPath,
        DiagnosticsConfig config,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!Directory.Exists(analyzersPath))
        {
            return [];
        }

        var results = new List<DiscoveredAnalyzer>();

        foreach (string subDir in Directory.GetDirectories(analyzersPath))
        {
            string manifestPath = Path.Combine(subDir, "analyzer.yaml");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            string yaml = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
            AnalyzerYamlModel raw = YamlDeserializer.Deserialize<AnalyzerYamlModel>(yaml) ?? new AnalyzerYamlModel();

            string dirName = Path.GetFileName(subDir);
            string name = raw.Name ?? dirName;

            // Check enabled status from config
            if (config.AnalyzerSettings.TryGetValue(dirName, out AnalyzerSettingsEntry? entry) && !entry.Enabled)
            {
                continue;
            }

            var metadata = new AnalyzerMetadata(
                Name: name,
                Description: raw.Description,
                RequiredCollectors: raw.RequiredCollectors ?? [],
                OptionalCollectors: raw.OptionalCollectors,
                AgentName: raw.AgentName,
                DefaultSettings: raw.DefaultSettings ?? new Dictionary<string, object?>(StringComparer.Ordinal));

            IReadOnlyDictionary<string, object?> mergedSettings = MergeAnalyzerSettings(metadata.DefaultSettings, dirName, config);

            results.Add(new DiscoveredAnalyzer(
                Name: name,
                Directory: subDir,
                Metadata: metadata,
                MergedSettings: mergedSettings));
        }

        return results;
    }
#pragma warning restore CA1822

    private static CollectorSettings MergeCollectorSettings(
        IReadOnlyDictionary<string, object?> defaults,
        string dirName,
        DiagnosticsConfig config)
    {
        var merged = new Dictionary<string, object?>(defaults, StringComparer.Ordinal);

        // Override with config settings (excluding Enabled)
        if (config.CollectorSettings.TryGetValue(dirName, out CollectorSettingsEntry? entry))
        {
            ApplyIfNotNull(merged, "MaxCollectSec", entry.MaxCollectSec);
            ApplyIfNotNull(merged, "StopTimeoutSec", entry.StopTimeoutSec);
            ApplyIfNotNull(merged, "ExportTimeoutSec", entry.ExportTimeoutSec);
            ApplyIfNotNull(merged, "BufferSizeMB", entry.BufferSizeMB);
            ApplyIfNotNull(merged, "MaxStacks", entry.MaxStacks);
        }

        // Inject PerfViewExePath from config
        if (config.PerfViewExePath is not null)
        {
            merged["PerfViewExePath"] = config.PerfViewExePath;
        }

        return new CollectorSettings(merged);
    }

    private static Dictionary<string, object?> MergeAnalyzerSettings(
        IReadOnlyDictionary<string, object?> defaults,
        string dirName,
        DiagnosticsConfig config)
    {
        var merged = new Dictionary<string, object?>(defaults, StringComparer.Ordinal);

        if (config.AnalyzerSettings.TryGetValue(dirName, out AnalyzerSettingsEntry? entry))
        {
            ApplyIfNotNull(merged, "Model", entry.Model);
            ApplyIfNotNull(merged, "MaxStacks", entry.MaxStacks);
        }

        return merged;
    }

    private static void ApplyIfNotNull(Dictionary<string, object?> dict, string key, object? value)
    {
        if (value is not null)
        {
            dict[key] = value;
        }
    }

    // ── YAML deserialization models ─────────────────────────────────────

    private sealed class CollectorYamlModel
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Group { get; set; }
        public bool RequiresAdmin { get; set; }
        public string? OverheadImpact { get; set; }
        public Dictionary<string, object?>? DefaultSettings { get; set; }
    }

    private sealed class AnalyzerYamlModel
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string>? RequiredCollectors { get; set; }
        public List<string>? OptionalCollectors { get; set; }
        public string? AgentName { get; set; }
        public Dictionary<string, object?>? DefaultSettings { get; set; }
    }
}
