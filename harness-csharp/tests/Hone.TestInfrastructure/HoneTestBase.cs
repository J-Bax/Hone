using Xunit.Abstractions;

namespace Hone.TestInfrastructure;

/// <summary>
/// Base class for all Hone test fixtures.
/// Provides shared setup, teardown, and utility methods.
/// </summary>
public abstract class HoneTestBase : IDisposable
{
    private static readonly string SolutionRoot = FindSolutionRoot();

    /// <summary>
    /// Gets a unique per-test temporary directory (similar to Pester TestDrive).
    /// </summary>
    protected string TempDir { get; }

    /// <summary>
    /// Gets the xUnit output helper for capturing test output.
    /// </summary>
    protected ITestOutputHelper Output { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="HoneTestBase"/>.
    /// </summary>
    protected HoneTestBase(ITestOutputHelper output)
    {
        Output = output;
        TempDir = Path.Combine(Path.GetTempPath(), $"Hone_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDir);
    }

    /// <summary>
    /// Creates a subdirectory under <see cref="TempDir"/> with the given name,
    /// optionally configured via a <see cref="TargetBuilder"/>.
    /// </summary>
    protected string CreateTargetDir(string name, Action<TargetBuilder>? configure = null)
    {
        string targetPath = Path.Combine(TempDir, name);
        Directory.CreateDirectory(targetPath);

        if (configure is not null)
        {
            var builder = new TargetBuilder(targetPath);
            configure(builder);
        }

        return targetPath;
    }

    /// <summary>
    /// Copies a fixture directory from test-fixtures/ into <see cref="TempDir"/>.
    /// Returns the path to the copy.
    /// </summary>
    protected string CopyFixtureTarget(string fixtureName)
    {
        string sourcePath = Path.Combine(SolutionRoot, "test-fixtures", fixtureName);

        if (!Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException(
                $"Fixture directory not found: {sourcePath}");
        }

        string destPath = Path.Combine(TempDir, fixtureName);
        CopyDirectoryRecursive(sourcePath, destPath);
        return destPath;
    }

    /// <summary>
    /// Initializes a git repository at the given path and returns a <see cref="GitTestRepo"/> helper.
    /// </summary>
    protected static GitTestRepo InitGitRepo(string path)
    {
        Directory.CreateDirectory(path);

        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = "init",
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _ = process.Start();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            string error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"git init failed with exit code {process.ExitCode}: {error}");
        }

        return new GitTestRepo(path);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases managed and unmanaged resources.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            CleanupTempDir();
        }
    }

    private void CleanupTempDir()
    {
        if (!Directory.Exists(TempDir))
        {
            return;
        }

        try
        {
            // Git creates read-only files; clear attributes before deleting
            foreach (string file in Directory.EnumerateFiles(TempDir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(TempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup — temp directory will be cleaned up by OS
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup — temp directory will be cleaned up by OS
        }
    }

    private static string FindSolutionRoot()
    {
        // Walk up from the assembly location to find the solution root
        // (the directory containing Hone.slnx or test-fixtures/)
        string? directory = AppContext.BaseDirectory;

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory, "test-fixtures")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        // Fallback: use the assembly base directory
        return AppContext.BaseDirectory;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
            CopyDirectoryRecursive(subDir, destSubDir);
        }
    }
}
