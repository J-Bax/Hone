using FluentAssertions;
using Hone.Orchestration.Artifacts;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Orchestration.Tests.Artifacts;

public sealed class ArtifactStagerTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private const string ResultsPath = ".hone/results";
    private const int Experiment = 1;

    /// <summary>
    /// Creates the experiment directory tree under a fresh temp target,
    /// writing empty files for the given relative paths.
    /// </summary>
    private string SetupExperiment(string testName, params string[] relativeFiles)
    {
        string targetDir = CreateTargetDir(testName);
        string experimentDir = Path.Combine(targetDir, ResultsPath, $"experiment-{Experiment}");
        Directory.CreateDirectory(experimentDir);

        foreach (string rel in relativeFiles)
        {
            string fullPath = Path.Combine(experimentDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.Create(fullPath).Dispose();
        }

        return targetDir;
    }

    /// <summary>
    /// Creates a directory under the experiment dir (for iterations/, etc.).
    /// </summary>
    private static void CreateExperimentSubDir(string targetDir, string subDir)
    {
        string path = Path.Combine(targetDir, ResultsPath, $"experiment-{Experiment}", subDir);
        Directory.CreateDirectory(path);
    }

    /// <summary>
    /// Creates a directory at the run level (e.g. metadata/).
    /// </summary>
    private static void CreateRunLevelDir(string targetDir, string subDir)
    {
        string path = Path.Combine(targetDir, ResultsPath, subDir);
        Directory.CreateDirectory(path);
    }

    /// <summary>
    /// Creates a file at the run level (e.g. run-metadata.json).
    /// </summary>
    private static void CreateRunLevelFile(string targetDir, string fileName)
    {
        string path = Path.Combine(targetDir, ResultsPath, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Create(path).Dispose();
    }

    [Fact]
    public void StageArtifacts_CopiesLogs()
    {
        // Arrange — build.log, e2e-tests.log, k6.log
        string targetDir = SetupExperiment("copies-logs", "build.log", "e2e-tests.log", "k6.log");

        // Act
        IReadOnlyList<string> paths = ArtifactStager.CollectArtifactPaths(targetDir, ResultsPath, Experiment);

        // Assert
        _ = paths.Should().Contain(".hone/results/experiment-1/build.log");
        _ = paths.Should().Contain(".hone/results/experiment-1/e2e-tests.log");
        _ = paths.Should().Contain(".hone/results/experiment-1/k6.log");
    }

    [Fact]
    public void StageArtifacts_CopiesMetrics()
    {
        // Arrange — only the median k6 summary is staged, per-run files are skipped
        string targetDir = SetupExperiment("copies-metrics",
            "k6-summary.json", "k6-summary-run1.json", "k6-summary-run2.json");

        // Act
        IReadOnlyList<string> paths = ArtifactStager.CollectArtifactPaths(targetDir, ResultsPath, Experiment);

        // Assert — only median
        _ = paths.Should().Contain(".hone/results/experiment-1/k6-summary.json");
        _ = paths.Should().NotContain(".hone/results/experiment-1/k6-summary-run1.json");
        _ = paths.Should().NotContain(".hone/results/experiment-1/k6-summary-run2.json");
    }

    [Fact]
    public void StageArtifacts_CopiesTrx()
    {
        // Arrange — e2e-results.trx
        string targetDir = SetupExperiment("copies-trx", "e2e-results.trx");

        // Act
        IReadOnlyList<string> paths = ArtifactStager.CollectArtifactPaths(targetDir, ResultsPath, Experiment);

        // Assert
        _ = paths.Should().Contain(".hone/results/experiment-1/e2e-results.trx");
    }

    [Fact]
    public void StageArtifacts_CollectsIterationLog()
    {
        // Arrange
        string targetDir = SetupExperiment("iteration-log", "iteration-log.json");

        // Act
        IReadOnlyList<string> paths = ArtifactStager.CollectArtifactPaths(targetDir, ResultsPath, Experiment);

        // Assert
        _ = paths.Should().Contain(".hone/results/experiment-1/iteration-log.json");
    }

    [Fact]
    public void StageArtifacts_CollectsIterationsDir()
    {
        // Arrange
        string targetDir = SetupExperiment("iterations-dir");
        CreateExperimentSubDir(targetDir, "iterations");

        // Act
        IReadOnlyList<string> paths = ArtifactStager.CollectArtifactPaths(targetDir, ResultsPath, Experiment);

        // Assert
        _ = paths.Should().Contain(".hone/results/experiment-1/iterations/");
    }

    [Fact]
    public void StageArtifacts_CounterDataNotStaged()
    {
        // Arrange — raw counter files are excluded (diagnostic summaries capture this data)
        string targetDir = SetupExperiment("counter-data", "dotnet-counters.json", "dotnet-counters.csv");

        // Act
        IReadOnlyList<string> paths = ArtifactStager.CollectArtifactPaths(targetDir, ResultsPath, Experiment);

        // Assert — raw counter files should not be staged
        _ = paths.Should().NotContain(".hone/results/experiment-1/dotnet-counters.json");
        _ = paths.Should().NotContain(".hone/results/experiment-1/dotnet-counters.csv");
    }

    [Fact]
    public void StageArtifacts_CollectsDiagnosticOutputs()
    {
        // Arrange — diagnostic summaries and analyzer outputs
        string targetDir = SetupExperiment("diagnostics",
            "diagnostics/dotnet-counters/dotnet-counters.json",
            "diagnostics/perfview-gc/gc-report.json",
            "diagnostics/cpu-hotspots/perf-prompt.md",
            "diagnostics/cpu-hotspots/perf-response.json",
            "diagnostics/memory-gc/gc-prompt.md",
            "diagnostics/memory-gc/gc-response.json");

        // Act
        IReadOnlyList<string> paths = ArtifactStager.CollectArtifactPaths(targetDir, ResultsPath, Experiment);

        // Assert — summaries
        _ = paths.Should().Contain(".hone/results/experiment-1/diagnostics/dotnet-counters/dotnet-counters.json");
        _ = paths.Should().Contain(".hone/results/experiment-1/diagnostics/perfview-gc/gc-report.json");

        // Assert — analyzer outputs
        _ = paths.Should().Contain(".hone/results/experiment-1/diagnostics/cpu-hotspots/perf-prompt.md");
        _ = paths.Should().Contain(".hone/results/experiment-1/diagnostics/cpu-hotspots/perf-response.json");
        _ = paths.Should().Contain(".hone/results/experiment-1/diagnostics/memory-gc/gc-prompt.md");
        _ = paths.Should().Contain(".hone/results/experiment-1/diagnostics/memory-gc/gc-response.json");
    }

    [Fact]
    public void StageArtifacts_CollectsMetadataDir()
    {
        // Arrange
        string targetDir = SetupExperiment("metadata-dir");
        CreateRunLevelDir(targetDir, "metadata");

        // Act
        IReadOnlyList<string> paths = ArtifactStager.CollectArtifactPaths(targetDir, ResultsPath, Experiment);

        // Assert
        _ = paths.Should().Contain(".hone/results/metadata/");
    }

    [Fact]
    public void StageArtifacts_CollectsRunMetadata()
    {
        // Arrange
        string targetDir = SetupExperiment("run-metadata");
        CreateRunLevelFile(targetDir, "run-metadata.json");

        // Act
        IReadOnlyList<string> paths = ArtifactStager.CollectArtifactPaths(targetDir, ResultsPath, Experiment);

        // Assert
        _ = paths.Should().Contain(".hone/results/run-metadata.json");
    }

    [Fact]
    public void StageArtifacts_MissingExperimentDir_ReturnsEmpty()
    {
        // Arrange — targetDir exists but no experiment directory
        string targetDir = CreateTargetDir("missing-experiment");

        // Act
        IReadOnlyList<string> paths = ArtifactStager.CollectArtifactPaths(targetDir, ResultsPath, Experiment);

        // Assert
        _ = paths.Should().BeEmpty();
    }

    [Fact]
    public void StageArtifacts_MixedPresence_OnlyExistingFiles()
    {
        // Arrange — only some files exist
        string targetDir = SetupExperiment("mixed-presence", "build.log", "k6.log");
        CreateExperimentSubDir(targetDir, "iterations");
        CreateRunLevelFile(targetDir, "run-metadata.json");

        // Act
        IReadOnlyList<string> paths = ArtifactStager.CollectArtifactPaths(targetDir, ResultsPath, Experiment);

        // Assert — present files
        _ = paths.Should().Contain(".hone/results/experiment-1/build.log");
        _ = paths.Should().Contain(".hone/results/experiment-1/k6.log");
        _ = paths.Should().Contain(".hone/results/experiment-1/iterations/");
        _ = paths.Should().Contain(".hone/results/run-metadata.json");

        // Assert — absent files should not appear
        _ = paths.Should().NotContain(".hone/results/experiment-1/e2e-tests.log");
        _ = paths.Should().NotContain(".hone/results/experiment-1/e2e-results.trx");
        _ = paths.Should().NotContain(".hone/results/experiment-1/analysis-prompt.md");
        _ = paths.Should().NotContain(".hone/results/experiment-1/dotnet-counters.json");
        _ = paths.Should().NotContain(".hone/results/metadata/");
    }

    [Fact]
    public void StageArtifacts_K6LogGlobs_NotStaged()
    {
        // Arrange — per-iteration k6 log files are no longer staged (main k6.log is in AnalysisArtifacts)
        string targetDir = SetupExperiment("k6-globs",
            "k6-iter1.log", "k6-iter2.log", "k6-final.log");

        // Act
        IReadOnlyList<string> paths = ArtifactStager.CollectArtifactPaths(targetDir, ResultsPath, Experiment);

        // Assert — per-iteration logs should not be staged
        _ = paths.Should().NotContain(".hone/results/experiment-1/k6-iter1.log");
        _ = paths.Should().NotContain(".hone/results/experiment-1/k6-iter2.log");
        _ = paths.Should().NotContain(".hone/results/experiment-1/k6-final.log");
    }

    [Fact]
    public void StageArtifacts_AllArtifactsPresent_CollectsAll()
    {
        // Arrange — comprehensive scenario with all artifact types
        string targetDir = SetupExperiment("all-artifacts",
            "analysis-prompt.md", "analysis-response.json", "classification-response.json",
            "fix-prompt.md", "fix-response.md", "root-cause.md",
            "build.log", "e2e-tests.log", "e2e-results.trx", "k6.log",
            "iteration-log.json",
            "k6-summary.json", "k6-summary-run1.json", "k6-summary-run2.json",
            "dotnet-counters.json", "dotnet-counters.csv",
            "diagnostics/dotnet-counters/dotnet-counters.json",
            "diagnostics/perfview-gc/gc-report.json",
            "diagnostics/cpu-hotspots/perf-prompt.md",
            "diagnostics/cpu-hotspots/perf-response.json",
            "diagnostics/memory-gc/gc-prompt.md",
            "diagnostics/memory-gc/gc-response.json");
        CreateExperimentSubDir(targetDir, "iterations");
        CreateRunLevelDir(targetDir, "metadata");
        CreateRunLevelFile(targetDir, "run-metadata.json");

        // Act
        IReadOnlyList<string> paths = ArtifactStager.CollectArtifactPaths(targetDir, ResultsPath, Experiment);

        // Assert — should contain key categories (agent output, diagnostics, metadata)
        _ = paths.Should().HaveCountGreaterThanOrEqualTo(16);
        _ = paths.Should().Contain(p => p.Contains("build.log"));
        _ = paths.Should().Contain(p => p.Contains("iterations/"));
        _ = paths.Should().Contain(p => p.EndsWith("k6-summary.json"));
        _ = paths.Should().Contain(p => p.Contains("diagnostics/"));
        _ = paths.Should().Contain(p => p.Contains("metadata/"));
        _ = paths.Should().Contain(p => p.Contains("run-metadata.json"));

        // Assert — heavy artifacts excluded
        _ = paths.Should().NotContain(p => p.Contains("dotnet-counters.json") && !p.Contains("diagnostics/"));
        _ = paths.Should().NotContain(p => p.Contains("dotnet-counters.csv"));
        _ = paths.Should().NotContain(p => p.Contains("k6-summary-run"));
    }

    [Fact]
    public void StageArtifacts_ForwardSlashesInPaths()
    {
        // Arrange
        string targetDir = SetupExperiment("forward-slashes", "build.log");

        // Act
        IReadOnlyList<string> paths = ArtifactStager.CollectArtifactPaths(targetDir, ResultsPath, Experiment);

        // Assert — no backslashes in any path
        foreach (string p in paths)
        {
            _ = p.Should().NotContain("\\", because: "git paths must use forward slashes");
        }
    }
}
