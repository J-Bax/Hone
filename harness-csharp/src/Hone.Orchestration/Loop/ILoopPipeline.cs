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
}
