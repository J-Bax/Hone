using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Agents.Preparation;

/// <summary>
/// Gathers lightweight pre-probe data from the target directory using the
/// filesystem and optional <see cref="IProcessRunner"/> for git commands.
/// </summary>
internal static class PreProber
{
    private const int MaxProjectFileHits = 10;
    private const int MaxDirectoryDepth = 3;
    private const int MaxTopLevelEntries = 30;

    private static readonly (string Name, string Pattern)[] ProjectFilePatterns =
    [
        ("dotnet-sln", "*.sln"),
        ("dotnet-csproj", "*.csproj"),
        ("dotnet-global", "global.json"),
        ("node-package", "package.json"),
        ("node-tsconfig", "tsconfig.json"),
        ("go-mod", "go.mod"),
        ("python-req", "requirements.txt"),
        ("python-pyproj", "pyproject.toml"),
        ("rust-cargo", "Cargo.toml"),
        ("java-maven", "pom.xml"),
        ("java-gradle", "build.gradle"),
        ("docker-compose", "docker-compose.yml"),
        ("dockerfile", "Dockerfile"),
        ("k6-scenario", "*.js"),
    ];

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "packages",
    };

    /// <summary>
    /// Runs the full pre-probe: git info, project file detection, directory
    /// listing, and .hone directory check.
    /// </summary>
    internal static async Task<PreProbeData> ProbeAsync(
        string targetPath,
        IProcessRunner? processRunner,
        CancellationToken ct)
    {
        string fullPath = Path.GetFullPath(targetPath);

        GitInfo git = processRunner is not null
            ? await ProbeGitAsync(fullPath, processRunner, ct).ConfigureAwait(false)
            : new GitInfo();

        string honeDir = Path.Combine(fullPath, ".hone");
        bool honeExists = Directory.Exists(honeDir);

        var data = new PreProbeData
        {
            TargetPath = fullPath,
            Git = git,
            ProjectFiles = ScanProjectFiles(fullPath),
            TopLevelDirs = ListTopLevelDirectories(fullPath),
            TopLevelFiles = ListTopLevelFiles(fullPath),
            ExistingHoneDir = honeExists,
            HoneDirContents = honeExists ? ListHoneDirContents(honeDir) : null,
        };

        return data;
    }

    private static async Task<GitInfo> ProbeGitAsync(
        string targetPath,
        IProcessRunner processRunner,
        CancellationToken ct)
    {
        var info = new GitInfo();

        ProcessResult revParseResult = await processRunner.RunAsync(
            "git",
            ["rev-parse", "--git-dir"],
            workingDirectory: targetPath,
            timeout: TimeSpan.FromSeconds(10),
            ct: ct).ConfigureAwait(false);

        if (!revParseResult.Success || revParseResult.ExitCode != 0)
        {
            return info;
        }

        info.IsGitRepo = true;

        ProcessResult remoteResult = await processRunner.RunAsync(
            "git",
            ["remote", "-v"],
            workingDirectory: targetPath,
            timeout: TimeSpan.FromSeconds(10),
            ct: ct).ConfigureAwait(false);

        if (remoteResult.Success && !string.IsNullOrWhiteSpace(remoteResult.Output))
        {
            string firstLine = remoteResult.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
            // "origin\thttps://github.com/repo.git (fetch)" → split on tab, take URL part
            string[] parts = firstLine.Split('\t', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                info.RemoteUrl = parts[1]
                    .Replace("(fetch)", "", StringComparison.Ordinal)
                    .Replace("(push)", "", StringComparison.Ordinal)
                    .Trim();
            }
        }

        ProcessResult headResult = await processRunner.RunAsync(
            "git",
            ["symbolic-ref", "refs/remotes/origin/HEAD"],
            workingDirectory: targetPath,
            timeout: TimeSpan.FromSeconds(10),
            ct: ct).ConfigureAwait(false);

        if (headResult.Success && !string.IsNullOrWhiteSpace(headResult.Output))
        {
            string headRef = headResult.Output.Trim();
            const string Prefix = "refs/remotes/origin/";
            if (headRef.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                info.DefaultBranch = headRef[Prefix.Length..];
            }
        }

        return info;
    }

    private static Dictionary<string, List<string>> ScanProjectFiles(string targetPath)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        int targetPathLength = targetPath.Length;

        foreach ((string name, string pattern) in ProjectFilePatterns)
        {
            var hits = new List<string>();
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = MaxDirectoryDepth,
                    IgnoreInaccessible = true,
                    MatchCasing = MatchCasing.CaseInsensitive,
                };

                foreach (string file in Directory.EnumerateFiles(targetPath, pattern, options))
                {
                    string relative = file[targetPathLength..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    hits.Add(relative);

                    if (hits.Count >= MaxProjectFileHits)
                    {
                        break;
                    }
                }
            }
#pragma warning disable CA1031 // Resilience: skip inaccessible directories
            catch (Exception)
            {
                // Silently ignore — matches PS -ErrorAction SilentlyContinue
            }
#pragma warning restore CA1031

            if (hits.Count > 0)
            {
                result[name] = hits;
            }
        }

        return result;
    }

    private static List<string> ListTopLevelDirectories(string targetPath)
    {
        var dirs = new List<string>();

        try
        {
            foreach (string dir in Directory.EnumerateDirectories(targetPath))
            {
                string name = Path.GetFileName(dir);

                if (name.StartsWith('.'))
                {
                    continue;
                }

                if (ExcludedDirs.Contains(name))
                {
                    continue;
                }

                dirs.Add(name);

                if (dirs.Count >= MaxTopLevelEntries)
                {
                    break;
                }
            }
        }
#pragma warning disable CA1031 // Resilience: skip inaccessible directories
        catch (Exception)
        {
            // Silently ignore
        }
#pragma warning restore CA1031

        return dirs;
    }

    private static List<string> ListTopLevelFiles(string targetPath)
    {
        var files = new List<string>();

        try
        {
            foreach (string file in Directory.EnumerateFiles(targetPath))
            {
                string name = Path.GetFileName(file);

                // Non-hidden + .gitignore
                if (name.StartsWith('.') && !string.Equals(name, ".gitignore", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                files.Add(name);

                if (files.Count >= MaxTopLevelEntries)
                {
                    break;
                }
            }
        }
#pragma warning disable CA1031 // Resilience: skip inaccessible directories
        catch (Exception)
        {
            // Silently ignore
        }
#pragma warning restore CA1031

        return files;
    }

    private static List<string> ListHoneDirContents(string honeDir)
    {
        var contents = new List<string>();
        int baselen = honeDir.Length;

        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(honeDir, "*", SearchOption.AllDirectories))
            {
                string relative = entry[baselen..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                contents.Add(relative);
            }
        }
#pragma warning disable CA1031 // Resilience: skip inaccessible paths
        catch (Exception)
        {
            // Silently ignore
        }
#pragma warning restore CA1031

        return contents;
    }
}
