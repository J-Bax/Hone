using System.Text.Json;

namespace Hone.Cli;

internal static class AtomicFileWriter
{
    internal static Task WriteJsonAsync<TValue>(
        string path,
        TValue value,
        JsonSerializerOptions options,
        CancellationToken ct)
        where TValue : notnull
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, options);
        return WriteBytesAsync(path, bytes, ct);
    }

    internal static Task WriteBytesAsync(
        string path,
        byte[] bytes,
        CancellationToken ct) =>
        WriteBytesAsync(
            path,
            bytes,
            static (tempPath, tempBytes, token) => File.WriteAllBytesAsync(tempPath, tempBytes, token),
            static (tempPath, destinationPath) => File.Move(tempPath, destinationPath, overwrite: true),
            ct);

    internal static async Task WriteBytesAsync(
        string path,
        byte[] bytes,
        Func<string, byte[], CancellationToken, Task> writeTempFileAsync,
        Action<string, string> moveFile,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(writeTempFileAsync);
        ArgumentNullException.ThrowIfNull(moveFile);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = path + ".tmp";
        await writeTempFileAsync(tempPath, bytes, ct).ConfigureAwait(false);
        moveFile(tempPath, path);
    }
}
