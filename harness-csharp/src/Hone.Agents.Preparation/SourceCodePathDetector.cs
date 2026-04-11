namespace Hone.Agents.Preparation;

/// <summary>
/// Detects source code directories within a project by scanning for files that
/// match stack-appropriate glob patterns. Used by <see cref="PreProber"/> to
/// populate <see cref="PreProbeData.DetectedSourceCodePaths"/>.
/// </summary>
internal static class SourceCodePathDetector
{
    /// <summary>Maximum directory depth to scan (relative to the project root).</summary>
    internal const int MaxScanDepth = 5;

    /// <summary>Maximum number of source paths to return.</summary>
    internal const int MaxPaths = 50;

    private static readonly HashSet<string> ExcludedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Build / output
        "bin", "obj", "out", "build", "dist", "publish", "artifacts",
        // Dependencies
        "node_modules", "packages", ".nuget", "vendor", ".venv", "venv", "__pycache__",
        // IDE / tooling
        ".vs", ".vscode", ".idea", ".git", ".github", ".hone",
        // Web static assets
        "wwwroot", "static", "public",
        // Migrations (EF Core, Flyway, Alembic, etc.)
        "Migrations", "migrations",
    };

    private static readonly HashSet<string> TestDirIndicators = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "spec", "specs", "xunit", "nunit",
        "jest", "mocha", "cypress", "playwright",
        "test-fixtures", "testfixtures",
    };

    private static readonly Dictionary<string, StackGlobInfo> StackGlobs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dotnet"] = new("*.cs", ["*.Designer.cs", "*.g.cs", "*.AssemblyInfo.cs", "GlobalUsings.cs"]),
        ["node"] = new("*.ts", ["*.d.ts", "*.spec.ts", "*.test.ts", "*.spec.js", "*.test.js"]),
        ["go"] = new("*.go", ["*_test.go"]),
        ["python"] = new("*.py", ["test_*.py", "*_test.py", "conftest.py", "setup.py"]),
        ["rust"] = new("*.rs", []),
        ["java"] = new("*.java", []),
    };

    /// <summary>
    /// Detects source code directories under <paramref name="projectRootPath"/>.
    /// </summary>
    /// <param name="projectRootPath">Absolute path to the project root (typically the repo root or ProjectPath).</param>
    /// <param name="projectFiles">Project file categories from <see cref="PreProber"/>.</param>
    /// <returns>
    /// A <see cref="DetectionResult"/> containing relative paths (to <paramref name="projectRootPath"/>)
    /// of directories that contain source files, and the inferred source file glob.
    /// </returns>
    internal static DetectionResult Detect(
        string projectRootPath,
        IReadOnlyDictionary<string, List<string>> projectFiles)
    {
        string stack = InferStack(projectFiles);
        if (!StackGlobs.TryGetValue(stack, out StackGlobInfo? globInfo))
        {
            return DetectionResult.Empty;
        }

        var sourceDirs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        ScanDirectory(projectRootPath, projectRootPath, globInfo, sourceDirs, depth: 0);

        List<string> paths = [.. sourceDirs.Take(MaxPaths)];

        return new DetectionResult(paths, globInfo.PrimaryGlob, stack);
    }

    private static void ScanDirectory(
        string rootPath,
        string currentPath,
        StackGlobInfo globInfo,
        SortedSet<string> results,
        int depth)
    {
        if (depth > MaxScanDepth || results.Count >= MaxPaths)
        {
            return;
        }

        try
        {
            string dirName = Path.GetFileName(currentPath);

            // Skip excluded and test directories (except at root level)
            if (depth > 0)
            {
                if (ExcludedDirNames.Contains(dirName))
                {
                    return;
                }

                if (IsTestDirectory(dirName))
                {
                    return;
                }
            }

            // Check if this directory has qualifying source files
            bool hasSourceFiles = HasMatchingSourceFiles(currentPath, globInfo);

            if (hasSourceFiles)
            {
                string relative = Path.GetRelativePath(rootPath, currentPath);

                // Don't add root itself (".")
                if (!string.Equals(relative, ".", StringComparison.Ordinal))
                {
                    _ = results.Add(NormalizePath(relative));
                }
            }

            // Recurse into subdirectories
            foreach (string subDir in Directory.EnumerateDirectories(currentPath))
            {
                ScanDirectory(rootPath, subDir, globInfo, results, depth + 1);
            }
        }
        catch (IOException)
        {
            // Expected: inaccessible directories during filesystem enumeration
        }
        catch (UnauthorizedAccessException)
        {
            // Expected: insufficient permissions for filesystem enumeration
        }
    }

    private static bool HasMatchingSourceFiles(string directoryPath, StackGlobInfo globInfo)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(directoryPath, globInfo.PrimaryGlob))
            {
                string fileName = Path.GetFileName(file);

                if (!IsExcludedFile(fileName, globInfo.ExcludedPatterns))
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
            // Expected: inaccessible files during filesystem enumeration
        }
        catch (UnauthorizedAccessException)
        {
            // Expected: insufficient permissions for filesystem enumeration
        }

        return false;
    }

    private static bool IsExcludedFile(string fileName, IReadOnlyList<string> excludedPatterns)
    {
        foreach (string pattern in excludedPatterns)
        {
            if (MatchesSimpleGlob(fileName, pattern))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Matches a filename against a simple glob pattern supporting only leading/trailing wildcards.
    /// </summary>
    internal static bool MatchesSimpleGlob(string fileName, string pattern)
    {
        if (pattern.StartsWith('*') && pattern.EndsWith('*') && pattern.Length > 2)
        {
            string middle = pattern[1..^1];
            return fileName.Contains(middle, StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.StartsWith('*'))
        {
            string suffix = pattern[1..];
            return fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.EndsWith('*'))
        {
            string prefix = pattern[..^1];
            return fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // Handle mid-pattern wildcard: prefix*suffix (e.g., "test_*.py")
        int starIndex = pattern.IndexOf('*', StringComparison.Ordinal);
        if (starIndex >= 0)
        {
            string prefix = pattern[..starIndex];
            string suffix = pattern[(starIndex + 1)..];
            return fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                   fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                   fileName.Length >= prefix.Length + suffix.Length;
        }

        return string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTestDirectory(string dirName) =>
        TestDirIndicators.Contains(dirName) ||
        dirName.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) ||
        dirName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
        dirName.EndsWith("Test", StringComparison.OrdinalIgnoreCase) ||
        dirName.EndsWith(".Test", StringComparison.OrdinalIgnoreCase);

    internal static string InferStack(IReadOnlyDictionary<string, List<string>> projectFiles)
    {
        // Priority order: .NET > Node > Go > Python > Rust > Java
        return projectFiles.ContainsKey("dotnet-sln") || projectFiles.ContainsKey("dotnet-csproj")
            ? "dotnet"
            : projectFiles.ContainsKey("node-package")
            ? "node"
            : projectFiles.ContainsKey("go-mod")
            ? "go"
            : projectFiles.ContainsKey("python-req") || projectFiles.ContainsKey("python-pyproj")
            ? "python"
            : projectFiles.ContainsKey("rust-cargo")
                ? "rust"
                : projectFiles.ContainsKey("java-maven") || projectFiles.ContainsKey("java-gradle")
                    ? "java"
                    : "unknown";
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');

    internal sealed record StackGlobInfo(string PrimaryGlob, IReadOnlyList<string> ExcludedPatterns);

    internal sealed record DetectionResult(
        IReadOnlyList<string> SourceCodePaths,
        string SourceFileGlob,
        string DetectedStack)
    {
        internal static DetectionResult Empty { get; } = new([], string.Empty, "unknown");
    }
}
