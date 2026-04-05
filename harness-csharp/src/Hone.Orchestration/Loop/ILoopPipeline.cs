using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Orchestration.Loop;

/// <summary>
/// Abstracts all external operations invoked by the optimization loop.
/// Each method corresponds to an operation that was a separate script call
/// in the PowerShell harness.
/// </summary>
internal interface ILoopPipeline
{
    /// <summary>Loads an existing baseline or creates one by running a full scale test.</summary>
    public Task<MetricSet> LoadOrCreateBaselineAsync(string targetDir, HoneConfig config, CancellationToken ct);

    /// <summary>Collects machine information (CPU, RAM, OS, .NET version).</summary>
    public Task<MachineInfo> GetMachineInfoAsync(CancellationToken ct);

    /// <summary>Runs diagnostic profiling and the analysis agent to discover opportunities.</summary>
    public Task<AnalysisResult> RunAnalysisAsync(AnalysisInput input, CancellationToken ct);

    /// <summary>Classifies a queue item to determine its scope (narrow vs architecture).</summary>
    public Task<ClassificationResult> ClassifyAsync(ClassificationInput input, CancellationToken ct);

    /// <summary>Executes a load test and returns the measured metrics.</summary>
    public Task<LoadTestResult> RunLoadTestAsync(LoadTestInput input, CancellationToken ct);

    /// <summary>Compares experiment metrics against baseline and previous reference.</summary>
    public ComparisonResult CompareMetrics(
        MetricSet current,
        MetricSet baseline,
        MetricSet? previous,
        int experiment,
        HoneConfig config);

    /// <summary>Pushes a branch to the remote code host.</summary>
    public Task<PushResult> PushBranchAsync(string targetDir, string branch, CancellationToken ct);

    /// <summary>Creates a pull request for an accepted experiment.</summary>
    public Task<PullRequestResult> CreatePullRequestAsync(CreatePrOptions options, CancellationToken ct);

    /// <summary>Stages artifact paths and commits them (amend or new commit).</summary>
    public Task CommitArtifactsAsync(string targetDir, IReadOnlyList<string> paths, string message, CancellationToken ct);

    /// <summary>Checks out a branch in the working directory.</summary>
    public Task CheckoutAsync(string targetDir, string branch, CancellationToken ct);

    /// <summary>Loads run-metadata.json from disk, or returns <see langword="null"/> if not found.</summary>
    public Task<RunMetadata?> LoadRunMetadataAsync(string path, CancellationToken ct);

    /// <summary>Persists run-metadata.json to disk.</summary>
    public Task SaveRunMetadataAsync(string path, RunMetadata metadata, CancellationToken ct);
}
