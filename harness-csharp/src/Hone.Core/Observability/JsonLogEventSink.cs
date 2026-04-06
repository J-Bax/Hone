using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hone.Core.Observability;

/// <summary>
/// Appends each <see cref="HoneEvent"/> as a single JSON line to a log file.
/// Rotates the log file when it exceeds the configured maximum size.
/// </summary>
/// <param name="logPath">Absolute path to the JSONL log file.</param>
/// <param name="maxFileSizeBytes">
/// Maximum file size in bytes before rotation.
/// Defaults to 50 MB.
/// </param>
public sealed class JsonLogEventSink(string logPath, long maxFileSizeBytes = 50L * 1024 * 1024) : IHoneEventSink
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _lock = new();
    private readonly string _logPath = logPath ?? throw new ArgumentNullException(nameof(logPath));
    private readonly long _maxFileSizeBytes = maxFileSizeBytes > 0
        ? maxFileSizeBytes
        : throw new ArgumentOutOfRangeException(nameof(maxFileSizeBytes), "Must be positive.");

    /// <inheritdoc/>
    public void Emit(HoneEvent honeEvent)
    {
        ArgumentNullException.ThrowIfNull(honeEvent);

        lock (_lock)
        {
            // Ensure directory exists
            string? dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            RotateIfNeeded();

            string json = JsonSerializer.Serialize<HoneEvent>(honeEvent, JsonOptions);
            File.AppendAllText(_logPath, json + "\n", Encoding.UTF8);
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_logPath))
        {
            return;
        }

        var fileInfo = new FileInfo(_logPath);
        if (fileInfo.Length > _maxFileSizeBytes)
        {
            string rotatedPath = _logPath + ".1";
            File.Move(_logPath, rotatedPath, overwrite: true);
        }
    }
}
