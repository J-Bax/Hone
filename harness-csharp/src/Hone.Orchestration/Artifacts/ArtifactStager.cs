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
        "critic-response.json",
        "root-cause.md",
        "build.log",
        "e2e-tests.log",
        "e2e-results.trx",
        "k6.log",
    ];

    // Only the median/final k6 summary — individual run files (k6-summary-run1…N)
    // are large and redundant once the median is selected.
    private static readonly string[] MeasurementSummaries =
    [
        "k6-summary.json",
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

        // Median k6 summary only (not per-run k6-summary-run*.json)
        CollectFixedFiles(paths, experimentDir, prefix, MeasurementSummaries);

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

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');
}
