namespace Hone.Orchestration.Artifacts;

/// <summary>
/// Collects the list of experiment artifact paths that should be staged for git commit.
/// Pure function — no side effects, no git calls. The caller passes the result to IVersionControl.
/// </summary>
internal static class ArtifactStager
{
    private static readonly string[] AnalysisArtifacts =
    [
        "analysis-prompt.md",
        "analysis-response.json",
        "classification-response.json",
        "fix-prompt.md",
        "fix-response.md",
        "root-cause.md",
        "build.log",
        "e2e-tests.log",
        "e2e-results.trx",
        "k6.log",
    ];

    private static readonly string[] CounterFiles =
    [
        "dotnet-counters.json",
        "dotnet-counters.csv",
    ];

    private static readonly string[] DiagnosticSummaries =
    [
        "diagnostics/dotnet-counters/dotnet-counters.json",
        "diagnostics/perfview-gc/gc-report.json",
    ];

    private static readonly string[] AnalyzerCategories =
    [
        "cpu-hotspots",
        "memory-gc",
    ];

    /// <summary>
    /// Returns relative paths (from <paramref name="targetDir"/>) for all existing experiment
    /// artifacts that should be staged for git commit. Paths use forward slashes for git compatibility.
    /// </summary>
    internal static IReadOnlyList<string> CollectArtifactPaths(string targetDir, string resultsPath, int experiment)
    {
        string experimentDir = Path.Combine(targetDir, resultsPath, $"experiment-{experiment}");

        if (!Directory.Exists(experimentDir))
        {
            return [];
        }

        string prefix = NormalizePath($"{resultsPath}/experiment-{experiment}");
        var paths = new List<string>();

        // Fixed analysis artifact files
        CollectFixedFiles(paths, experimentDir, prefix, AnalysisArtifacts);

        // Iteration log
        CollectFixedFiles(paths, experimentDir, prefix, ["iteration-log.json"]);

        // Iterations directory
        string iterationsDir = Path.Combine(experimentDir, "iterations");
        if (Directory.Exists(iterationsDir))
        {
            paths.Add($"{prefix}/iterations/");
        }

        // k6 log globs: k6-*.log
        CollectGlobFiles(paths, experimentDir, prefix, "k6-*.log");

        // k6 summary globs: k6-summary*.json
        CollectGlobFiles(paths, experimentDir, prefix, "k6-summary*.json");

        // Counter data files
        CollectFixedFiles(paths, experimentDir, prefix, CounterFiles);

        // Diagnostic collector summaries
        foreach (string summary in DiagnosticSummaries)
        {
            string fullPath = Path.Combine(experimentDir, summary.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
            {
                paths.Add($"{prefix}/{summary}");
            }
        }

        // Analyzer outputs (prompt/response files in diagnostics/{category}/)
        foreach (string category in AnalyzerCategories)
        {
            string analyzerDir = Path.Combine(experimentDir, "diagnostics", category);
            if (!Directory.Exists(analyzerDir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(analyzerDir))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith("-prompt.md", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("-response.json", StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add($"{prefix}/diagnostics/{category}/{name}");
                }
            }
        }

        // Run-level: metadata directory
        string resultsRoot = NormalizePath(resultsPath);
        string metadataDir = Path.Combine(targetDir, resultsPath, "metadata");
        if (Directory.Exists(metadataDir))
        {
            paths.Add($"{resultsRoot}/metadata/");
        }

        // Run-level: run-metadata.json
        string runMetadataFile = Path.Combine(targetDir, resultsPath, "run-metadata.json");
        if (File.Exists(runMetadataFile))
        {
            paths.Add($"{resultsRoot}/run-metadata.json");
        }

        return paths;
    }

    private static void CollectFixedFiles(List<string> paths, string experimentDir, string prefix, string[] fileNames)
    {
        foreach (string fileName in fileNames)
        {
            string fullPath = Path.Combine(experimentDir, fileName);
            if (File.Exists(fullPath))
            {
                paths.Add($"{prefix}/{fileName}");
            }
        }
    }

    private static void CollectGlobFiles(List<string> paths, string experimentDir, string prefix, string pattern)
    {
        foreach (string file in Directory.EnumerateFiles(experimentDir, pattern))
        {
            string name = Path.GetFileName(file);
            paths.Add($"{prefix}/{name}");
        }
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');
}
