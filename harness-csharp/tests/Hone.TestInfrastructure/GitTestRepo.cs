using System.Diagnostics;

namespace Hone.TestInfrastructure;

/// <summary>
/// Minimal git helper for test setup. Uses synchronous Process calls.
/// </summary>
public sealed class GitTestRepo
{
    /// <summary>
    /// Gets the path to the git repository.
    /// </summary>
    public string Path { get; }

    internal GitTestRepo(string path)
    {
        Path = path;
    }

    /// <summary>
    /// Configures user.name and user.email for the test repo.
    /// </summary>
    public void Configure(string name = "test", string email = "test@test.com")
    {
        RunGit($"config user.name \"{name}\"");
        RunGit($"config user.email \"{email}\"");
    }

    /// <summary>
    /// Stages all files and creates a commit.
    /// </summary>
    public void CommitAll(string message = "test commit")
    {
        RunGit("add -A");
        RunGit($"commit -m \"{message}\" --allow-empty");
    }

    /// <summary>
    /// Creates a new branch with the given name.
    /// </summary>
    public void CreateBranch(string name) => RunGit($"branch {name}");

    /// <summary>
    /// Checks out the specified branch.
    /// </summary>
    public void Checkout(string name) => RunGit($"checkout {name}");

#pragma warning disable RS0030 // Sync I/O is intentional in test infrastructure
    private void RunGit(string arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = Path,
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
                $"git {arguments} failed with exit code {process.ExitCode}: {error}");
        }
    }
#pragma warning restore RS0030
}
