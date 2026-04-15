using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Hone.Core.Config;

/// <summary>
/// Loads YAML configuration files and deserializes them into <see cref="HoneConfig"/>.
/// </summary>
public static class ConfigLoader
{
    /// <summary>
    /// Loads a YAML configuration file and deserializes it into a <see cref="HoneConfig"/>.
    /// </summary>
    /// <param name="yamlPath">Path to the YAML configuration file.</param>
    /// <returns>The deserialized configuration.</returns>
    /// <exception cref="FileNotFoundException">When the file does not exist.</exception>
    /// <exception cref="InvalidOperationException">When the YAML is malformed or cannot be deserialized.</exception>
    public static HoneConfig Load(string yamlPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(yamlPath);

        if (!File.Exists(yamlPath))
        {
            throw new FileNotFoundException(
                $"Configuration file not found: {yamlPath}", yamlPath);
        }

#pragma warning disable RS0030 // Sync File.ReadAllText is appropriate for one-time config load at startup
        string yaml = File.ReadAllText(yamlPath);
#pragma warning restore RS0030

        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new HoneConfig();
        }

        try
        {
            IDeserializer deserializer = new DeserializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .WithObjectFactory(new RecordAwareObjectFactory())
                .WithTypeMapping<IReadOnlyList<string>, List<string>>()
                .WithTypeMapping<IReadOnlyDictionary<string, CollectorSettingsEntry>, Dictionary<string, CollectorSettingsEntry>>()
                .WithTypeMapping<IReadOnlyDictionary<string, AnalyzerSettingsEntry>, Dictionary<string, AnalyzerSettingsEntry>>()
                .IgnoreUnmatchedProperties()
                .Build();

            HoneConfig? config = deserializer.Deserialize<HoneConfig>(yaml);
            return config ?? new HoneConfig();
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse YAML configuration from '{yamlPath}': {ex.Message}", ex);
        }
    }
}
