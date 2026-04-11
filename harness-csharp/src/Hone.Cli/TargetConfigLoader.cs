using Hone.Core.Config;
using Hone.Lifecycle.Hooks;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Hone.Cli;

/// <summary>
/// Loads the target project's <c>.hone/config.yaml</c> into a <see cref="TargetConfig"/>
/// to extract lifecycle hook definitions and optional diagnostic overrides.
/// </summary>
internal static class TargetConfigLoader
{
    /// <summary>
    /// Loads a YAML configuration file and deserializes it into a <see cref="TargetConfig"/>.
    /// </summary>
    internal static TargetConfig Load(string yamlPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(yamlPath);

        if (!File.Exists(yamlPath))
        {
            throw new FileNotFoundException(
                $"Target configuration file not found: {yamlPath}", yamlPath);
        }

#pragma warning disable RS0030 // Sync File.ReadAllText is appropriate for one-time config load at startup
        string yaml = File.ReadAllText(yamlPath);
#pragma warning restore RS0030

        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new TargetConfig();
        }

        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .WithTypeMapping<IReadOnlyDictionary<string, TargetHookConfig>, Dictionary<string, TargetHookConfig>>()
            .WithTypeMapping<IReadOnlyDictionary<string, CollectorSettingsEntry>, Dictionary<string, CollectorSettingsEntry>>()
            .WithTypeMapping<IReadOnlyDictionary<string, AnalyzerSettingsEntry>, Dictionary<string, AnalyzerSettingsEntry>>()
            .IgnoreUnmatchedProperties()
            .Build();

        TargetConfig? config = deserializer.Deserialize<TargetConfig>(yaml);
        return config ?? new TargetConfig();
    }
}
