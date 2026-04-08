using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Orchestration.Loop;

internal interface ILoopPipeline
{
    public Task<MetricSet> LoadOrCreateBaselineAsync(string targetDir, HoneConfig config, CancellationToken ct);
    public Task<MachineInfo> GetMachineInfoAsync(CancellationToken ct);
    public Task<AnalysisResult> RunAnalysisAsync(AnalysisInput input, CancellationToken ct);
    public Task<ClassificationResult> ClassifyAsync(ClassificationInput input, CancellationToken ct);
    public Task<LoadTestResult> RunLoadTestAsync(LoadTestInput input, CancellationToken ct);

    public ComparisonResult CompareMetrics(
        MetricSet current,
        MetricSet baseline,
        MetricSet? previous,
        int experiment,
        HoneConfig config);

    public Task<PushResult> PushBranchAsync(string targetDir, string branch, CancellationToken ct);
    public Task<PullRequestResult> CreatePullRequestAsync(CreatePrOptions options, CancellationToken ct);
    public Task CommitArtifactsAsync(string targetDir, IReadOnlyList<string> paths, string message, CancellationToken ct);
    public Task CheckoutAsync(string targetDir, string branch, CancellationToken ct);
    public Task<RunMetadata?> LoadRunMetadataAsync(string path, CancellationToken ct);
    public Task SaveRunMetadataAsync(string path, RunMetadata metadata, CancellationToken ct);

    /// <summary>
    /// Stops the target API process via the configured lifecycle hook.
    /// </summary>
    public Task<HookResult> StopTargetAsync(string targetDir, HoneConfig config, int experiment, CancellationToken ct);

    /// <summary>
    /// Starts the target API and waits for it to become healthy via configured lifecycle hooks.
    /// Dispatches the Start hook followed by the Ready hook.
    /// </summary>
    public Task<HookResult> StartTargetAsync(string targetDir, HoneConfig config, int experiment, CancellationToken ct);

    /// <summary>
    /// Runs the Prepare hook once per run before the experiment loop begins.
    /// Used for project-level setup (e.g. NuGet restore, codegen).
    /// </summary>
    public Task<HookResult> PrepareAsync(string targetDir, HoneConfig config, CancellationToken ct);
}
