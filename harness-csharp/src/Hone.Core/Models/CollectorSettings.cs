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

        try
        {
            return (T)Convert.ChangeType(raw, typeof(T), CultureInfo.InvariantCulture);
        }
        catch (InvalidCastException)
        {
            return defaultValue;
        }
        catch (FormatException)
        {
            return defaultValue;
        }
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
