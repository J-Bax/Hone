namespace Hone.Agents.Preparation;

/// <summary>
/// Result of writing scaffold files to disk.
/// </summary>
public sealed record ScaffoldWriteResult(
    IReadOnlyList<string> Written,
    IReadOnlyList<string> Skipped);

/// <summary>
/// Writes scaffold plan files to the target directory.
/// </summary>
public static class ScaffoldWriter
{
    /// <summary>
    /// Writes files from a <see cref="ScaffoldPlan"/> to the target directory.
    /// </summary>
    /// <param name="targetPath">Root directory of the target project.</param>
    /// <param name="plan">The scaffold plan containing file paths and contents.</param>
    /// <param name="force">When <see langword="true"/>, overwrite existing files.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ScaffoldWriteResult"/> listing written and skipped files.</returns>
    public static async Task<ScaffoldWriteResult> WriteAsync(
        string targetPath,
        ScaffoldPlan plan,
        bool force,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(plan);

        var written = new List<string>();
        var skipped = new List<string>();
        string normalizedTargetRoot = Path.GetFullPath(targetPath);

        if (!normalizedTargetRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            normalizedTargetRoot += Path.DirectorySeparatorChar;
        }

        if (plan.Files is null || plan.Files.Count == 0)
        {
            return new ScaffoldWriteResult(Written: written, Skipped: skipped);
        }

        foreach (KeyValuePair<string, string> entry in plan.Files)
        {
            ct.ThrowIfCancellationRequested();

            string relativePath = entry.Key;
            string content = entry.Value;

            if (Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException(
                    $"Scaffold file path '{relativePath}' must be a target-relative path and cannot be rooted.",
                    nameof(plan));
            }

            string fullPath = Path.GetFullPath(Path.Combine(targetPath, relativePath));

            if (!fullPath.StartsWith(normalizedTargetRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Scaffold file path '{relativePath}' escapes the target root '{targetPath}'.",
                    nameof(plan));
            }

            string? directory = Path.GetDirectoryName(fullPath);

            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(fullPath) && !force)
            {
                skipped.Add(relativePath);
                continue;
            }

            await File.WriteAllTextAsync(fullPath, content, ct).ConfigureAwait(false);
            written.Add(relativePath);
        }

        return new ScaffoldWriteResult(Written: written, Skipped: skipped);
    }
}
