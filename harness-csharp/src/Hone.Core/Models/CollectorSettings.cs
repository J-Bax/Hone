using System.Globalization;

namespace Hone.Core.Models;

/// <summary>
/// Merged settings dictionary used by collector plugins.
/// Defaults from metadata are overridden by per-collector config entries.
/// </summary>
public sealed record CollectorSettings(IReadOnlyDictionary<string, object?> Values)
{
    /// <summary>
    /// Gets the settings dictionary, defaulting to empty.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Values { get; init; } =
        Values ?? new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// Retrieves a typed setting value, falling back to <paramref name="defaultValue"/>.
    /// </summary>
    public T? Get<T>(string key, T? defaultValue = default)
    {
        if (!Values.TryGetValue(key, out object? raw) || raw is null)
        {
            return defaultValue;
        }

        if (raw is T typed)
        {
            return typed;
        }

        string? str = raw.ToString();
        if (str is null)
        {
            return defaultValue;
        }

        if (typeof(T) == typeof(int))
        {
            return int.TryParse(str, CultureInfo.InvariantCulture, out int i) ? (T)(object)i : defaultValue;
        }

        if (typeof(T) == typeof(long))
        {
            return long.TryParse(str, CultureInfo.InvariantCulture, out long l) ? (T)(object)l : defaultValue;
        }

        if (typeof(T) == typeof(double))
        {
            return double.TryParse(str, CultureInfo.InvariantCulture, out double d) ? (T)(object)d : defaultValue;
        }

        if (typeof(T) == typeof(bool))
        {
            return bool.TryParse(str, out bool b) ? (T)(object)b : defaultValue;
        }

        if (typeof(T) == typeof(string))
        {
            return (T)(object)str;
        }

        return defaultValue;
    }

    /// <summary>Maximum collection duration in seconds.</summary>
    public int MaxCollectSec => Get("MaxCollectSec", 150);

    /// <summary>ETW buffer size in MB.</summary>
    public int BufferSizeMB => Get("BufferSizeMB", 256);

    /// <summary>Stop timeout in seconds.</summary>
    public int StopTimeoutSec => Get("StopTimeoutSec", 300);

    /// <summary>Export timeout in seconds.</summary>
    public int ExportTimeoutSec => Get("ExportTimeoutSec", 300);

    /// <summary>Maximum number of stacks to capture.</summary>
    public int MaxStacks => Get("MaxStacks", 100);

    /// <summary>Path to the PerfView executable.</summary>
    public string? PerfViewExePath => Get<string>("PerfViewExePath");
}
