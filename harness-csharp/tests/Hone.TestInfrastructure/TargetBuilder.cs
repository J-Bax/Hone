namespace Hone.TestInfrastructure;

/// <summary>
/// Builder for creating a target directory structure in tests.
/// </summary>
public sealed class TargetBuilder
{
    private readonly string _path;

    internal TargetBuilder(string path)
    {
        _path = path;
    }

    /// <summary>
    /// Adds a file with the specified content under the target directory.
    /// </summary>
    public TargetBuilder AddFile(string relativePath, string content)
    {
        string fullPath = Path.Combine(_path, relativePath);
        string? directory = Path.GetDirectoryName(fullPath);

        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

#pragma warning disable RS0030 // Sync I/O is intentional in test infrastructure
        File.WriteAllText(fullPath, content);
#pragma warning restore RS0030

        return this;
    }

    /// <summary>
    /// Adds an empty subdirectory under the target directory.
    /// </summary>
    public TargetBuilder AddDirectory(string relativePath)
    {
        string fullPath = Path.Combine(_path, relativePath);
        Directory.CreateDirectory(fullPath);
        return this;
    }
}
