using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using Hone.Agents.Loop.Analysis;
using Hone.Agents.Loop.Classification;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Measurement.Comparison;
using Hone.Measurement.K6;
using Hone.Measurement.Orchestration;
using Hone.Orchestration.Loop;

using LoopAnalysisResult = Hone.Orchestration.Loop.AnalysisResult;
using LoopClassificationResult = Hone.Orchestration.Loop.ClassificationResult;

namespace Hone.Cli;

/// <summary>
/// Wires the real service implementations to the <see cref="ILoopPipeline"/> contract.
/// Each method delegates to the appropriate component from Phases 1-8.
/// </summary>
internal sealed class LoopPipelineAdapter : ILoopPipeline
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILoadTestRunner _loadTestRunner;
    private readonly AnalysisAgent _analysisAgent;
    private readonly ClassificationAgent _classificationAgent;
    private readonly ICodeHost _codeHost;
    private readonly IVersionControl _versionControl;
    private readonly HoneConfig _config;

    internal LoopPipelineAdapter(
        ILoadTestRunner loadTestRunner,
        AnalysisAgent analysisAgent,
        ClassificationAgent classificationAgent,
        ICodeHost codeHost,
        IVersionControl versionControl,
        HoneConfig config)
    {
        _loadTestRunner = loadTestRunner;
        _analysisAgent = analysisAgent;
        _classificationAgent = classificationAgent;
        _codeHost = codeHost;
        _versionControl = versionControl;
        _config = config;
    }

    /// <inheritdoc />
    public async Task<MetricSet> LoadOrCreateBaselineAsync(
        string targetDir, HoneConfig config, CancellationToken ct)
    {
        string resultsPath = config.Api.ResultsPath;
        string baselinePath = Path.Combine(targetDir, resultsPath, "baseline", "k6-summary.json");

        if (File.Exists(baselinePath))
        {
            MetricSet existing = await K6SummaryParser.ParseAsync(
                baselinePath, experiment: 0, run: 0, ct).ConfigureAwait(false);
            return existing;
        }

        string outputDir = Path.Combine(targetDir, resultsPath, "baseline");
        var baseUrl = new Uri(config.Api.BaseUrl);

        ScaleTestResult result = await ScaleTestOrchestrator.RunAsync(
            config.ScaleTest, _loadTestRunner, baseUrl, outputDir, experiment: 0, ct).ConfigureAwait(false);

        return result.Metrics ?? throw new InvalidOperationException("Baseline scale test produced no metrics.");
    }

    /// <inheritdoc />
    public Task<MachineInfo> GetMachineInfoAsync(CancellationToken ct)
    {
        string cpuName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")
                         ?? "Unknown CPU";
        int cpuCores = Environment.ProcessorCount;
        decimal? totalRamGb = null;
        string osVersion = RuntimeInformation.OSDescription;
        string dotnetVersion = RuntimeInformation.FrameworkDescription;

        long totalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (totalMemory > 0)
        {
            totalRamGb = Math.Round((decimal)totalMemory / (1024 * 1024 * 1024), 1);
        }

        var info = new MachineInfo(
            CpuName: cpuName,
            CpuCores: cpuCores,
            TotalRamGB: totalRamGb,
            OsVersion: osVersion,
            DotnetVersion: dotnetVersion);

        return Task.FromResult(info);
    }

    /// <inheritdoc />
    public async Task<LoopAnalysisResult> RunAnalysisAsync(AnalysisInput input, CancellationToken ct)
    {
        AnalysisContext context = AnalysisContextBuilder.Build(
            input.TargetDir,
            _config,
            counters: null,
            previousRcaExplanation: null,
            diagnosticReports: null);

        MetricSet current = input.ReferenceMetrics ?? input.BaselineMetrics;

        Hone.Agents.Loop.Analysis.AnalysisResult agentResult = await _analysisAgent.AnalyzeAsync(
            context,
            current,
            input.BaselineMetrics,
            comparison: null,
            input.Experiment,
            targetLabel: Path.GetFileName(input.TargetDir) ?? "target",
            workingDirectory: input.TargetDir,
            ct).ConfigureAwait(false);

        return new LoopAnalysisResult(
            Success: agentResult.Success,
            Opportunities: agentResult.Opportunities);
    }

    /// <inheritdoc />
    public async Task<LoopClassificationResult> ClassifyAsync(ClassificationInput input, CancellationToken ct)
    {
        Hone.Agents.Loop.Classification.ClassificationResult agentResult = await _classificationAgent.ClassifyAsync(
            input.FilePath,
            input.Explanation,
            targetLabel: Path.GetFileName(input.TargetDir) ?? "target",
            workingDirectory: input.TargetDir,
            ct).ConfigureAwait(false);

        return new LoopClassificationResult(
            Success: agentResult.Success,
            Scope: agentResult.Scope);
    }

    /// <inheritdoc />
    public async Task<LoadTestResult> RunLoadTestAsync(LoadTestInput input, CancellationToken ct)
    {
        string outputDir = Path.Combine(input.TargetDir, input.ResultsPath, $"experiment-{input.Experiment}");
        var baseUrl = new Uri(_config.Api.BaseUrl);

        ScaleTestResult result = await ScaleTestOrchestrator.RunAsync(
            _config.ScaleTest, _loadTestRunner, baseUrl, outputDir, input.Experiment, ct).ConfigureAwait(false);

        if (result.Metrics is null)
        {
            return new LoadTestResult(Success: false, Metrics: null, SummaryPath: null, Output: "No metrics produced");
        }

        return new LoadTestResult(
            Success: result.Success,
            Metrics: result.Metrics,
            SummaryPath: result.SummaryPath,
            Output: null);
    }

    /// <inheritdoc />
    public ComparisonResult CompareMetrics(
        MetricSet current,
        MetricSet baseline,
        MetricSet? previous,
        int experiment,
        HoneConfig config)
    {
        MetricSet reference = previous ?? baseline;

        return MetricComparer.Compare(
            current,
            reference,
            baseline,
            config.Tolerances);
    }

    /// <inheritdoc />
    public Task<PushResult> PushBranchAsync(string targetDir, string branch, CancellationToken ct) =>
        _codeHost.PushBranchAsync(targetDir, branch, ct);

    /// <inheritdoc />
    public Task<PullRequestResult> CreatePullRequestAsync(CreatePrOptions options, CancellationToken ct) =>
        _codeHost.CreatePullRequestAsync(options, ct);

    /// <inheritdoc />
    public Task CommitArtifactsAsync(
        string targetDir, IReadOnlyList<string> paths, string message, CancellationToken ct) =>
        _versionControl.CommitAsync(targetDir, message, paths, ct);

    /// <inheritdoc />
    public Task CheckoutAsync(string targetDir, string branch, CancellationToken ct) =>
        _versionControl.CheckoutAsync(targetDir, branch, create: false, ct);

    /// <inheritdoc />
    public async Task<RunMetadata?> LoadRunMetadataAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<RunMetadata>(bytes, MetadataJsonOptions);
    }

    /// <inheritdoc />
    public async Task SaveRunMetadataAsync(string path, RunMetadata metadata, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, MetadataJsonOptions);
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
    }
}
