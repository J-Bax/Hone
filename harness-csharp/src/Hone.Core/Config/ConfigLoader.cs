using System.Collections;
using System.Reflection;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization.ObjectFactories;

namespace Hone.Core.Config;

/// <summary>
/// Loads YAML configuration files and deserializes them into <see cref="HoneConfig"/>.
/// Replaces <c>Get-HoneConfig</c> from HoneHelpers.psm1.
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

    /// <summary>
    /// Object factory that supports C# records with all-default constructor parameters.
    /// YamlDotNet's <see cref="DefaultObjectFactory"/> requires a parameterless constructor,
    /// but positional records with default parameter values don't generate one.
    /// </summary>
    private sealed class RecordAwareObjectFactory : IObjectFactory
    {
        private readonly DefaultObjectFactory _inner = new();

        public object Create(Type type)
        {
            // Try constructor with all-default parameters (positional records)
            ConstructorInfo[] ctors = type.GetConstructors();
            if (ctors.Length > 0)
            {
                ConstructorInfo ctor = ctors[0];
                ParameterInfo[] parameters = ctor.GetParameters();
                if (parameters.Length > 0 && Array.TrueForAll(parameters, static p => p.HasDefaultValue))
                {
                    object?[] args = Array.ConvertAll(parameters, static p => p.DefaultValue);
                    return ctor.Invoke(args);
                }
            }

            return _inner.Create(type);
        }

        public object? CreatePrimitive(Type type) => _inner.CreatePrimitive(type);

        public bool GetDictionary(IObjectDescriptor descriptor, out IDictionary? dictionary, out Type[]? genericArguments)
            => _inner.GetDictionary(descriptor, out dictionary, out genericArguments);

        public Type GetValueType(Type type) => _inner.GetValueType(type);

        public void ExecuteOnDeserializing(object value) => _inner.ExecuteOnDeserializing(value);

        public void ExecuteOnDeserialized(object value) => _inner.ExecuteOnDeserialized(value);

        public void ExecuteOnSerializing(object value) => _inner.ExecuteOnSerializing(value);

        public void ExecuteOnSerialized(object value) => _inner.ExecuteOnSerialized(value);
    }
}

