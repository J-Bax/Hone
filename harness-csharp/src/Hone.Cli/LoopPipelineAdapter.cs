using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using Hone.Agents.Loop.Analysis;
using Hone.Agents.Loop.Classification;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Lifecycle.Hooks;
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
    private readonly HttpClient? _httpClient;
    private readonly LifecycleHookDispatcher? _hookDispatcher;
    private readonly TargetConfig? _targetConfig;

    internal LoopPipelineAdapter(
        ILoadTestRunner loadTestRunner,
        AnalysisAgent analysisAgent,
        ClassificationAgent classificationAgent,
        ICodeHost codeHost,
        IVersionControl versionControl,
        HoneConfig config,
        HttpClient? httpClient = null,
        LifecycleHookDispatcher? hookDispatcher = null,
        TargetConfig? targetConfig = null)
    {
        _loadTestRunner = loadTestRunner;
        _analysisAgent = analysisAgent;
        _classificationAgent = classificationAgent;
        _codeHost = codeHost;
        _versionControl = versionControl;
        _config = config;
        _httpClient = httpClient;
        _hookDispatcher = hookDispatcher;
        _targetConfig = targetConfig;
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
            config.ScaleTest, _loadTestRunner, baseUrl, outputDir, experiment: 0,
            BuildCooldownCallback(), ct).ConfigureAwait(false);

        MetricSet metrics = result.Metrics
            ?? throw new InvalidOperationException("Baseline scale test produced no metrics.");

        // Persist the selected median run's summary as the aggregated baseline file
        // so subsequent `run` invocations skip re-measurement.
        if (!string.IsNullOrEmpty(metrics.SummaryPath) && File.Exists(metrics.SummaryPath))
        {
            byte[] summaryBytes = await File.ReadAllBytesAsync(metrics.SummaryPath, ct)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(baselinePath, summaryBytes, ct)
                .ConfigureAwait(false);
        }

        return metrics;
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
        AnalysisContext context = await AnalysisContextBuilder.BuildAsync(
            input.TargetDir,
            _config,
            counters: null,
            previousRcaExplanation: null,
            diagnosticReports: null,
            ct: ct).ConfigureAwait(false);

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

        Func<CancellationToken, Task>? afterRunCallback = BuildCooldownCallback();

        ScaleTestResult result = await ScaleTestOrchestrator.RunAsync(
            _config.ScaleTest, _loadTestRunner, baseUrl, outputDir, input.Experiment,
            afterRunCallback, ct).ConfigureAwait(false);

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

    /// <inheritdoc />
    public async Task<HookResult> StopTargetAsync(
        string targetDir, HoneConfig config, int experiment, CancellationToken ct)
    {
        if (_hookDispatcher is null || _targetConfig is null)
        {
            return new HookResult(
                Success: true,
                Message: "Lifecycle hooks not configured — skipping stop",
                Duration: TimeSpan.Zero,
                Artifacts: [],
                BaseUrl: null);
        }

        ResolvedHook hook = HookResolver.Resolve("Stop", _targetConfig);
        var context = new HookContext(targetDir, config, BaseUrl: null, experiment);
        return await _hookDispatcher.DispatchAsync("Stop", hook, context, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HookResult> StartTargetAsync(
        string targetDir, HoneConfig config, int experiment, CancellationToken ct)
    {
        if (_hookDispatcher is null || _targetConfig is null)
        {
            return new HookResult(
                Success: true,
                Message: "Lifecycle hooks not configured — skipping start",
                Duration: TimeSpan.Zero,
                Artifacts: [],
                BaseUrl: null);
        }

        // Dispatch Start hook
        ResolvedHook startHook = HookResolver.Resolve("Start", _targetConfig);
        var startContext = new HookContext(targetDir, config, BaseUrl: null, experiment);
        HookResult startResult = await _hookDispatcher.DispatchAsync("Start", startHook, startContext, ct)
            .ConfigureAwait(false);

        if (!startResult.Success)
        {
            return startResult;
        }

        // Track the BaseUrl from the start result (DotnetStartHook returns it)
        Uri? startedBaseUrl = startResult.BaseUrl;

        // Dispatch Ready hook (health poll) using the BaseUrl from Start
        ResolvedHook readyHook = HookResolver.Resolve("Ready", _targetConfig);
        var readyContext = new HookContext(targetDir, config, BaseUrl: startedBaseUrl, experiment);
        HookResult readyResult = await _hookDispatcher.DispatchAsync("Ready", readyHook, readyContext, ct)
            .ConfigureAwait(false);

        if (!readyResult.Success)
        {
            return readyResult;
        }

        return startResult;
    }

    /// <inheritdoc />
    public async Task<HookResult> PrepareAsync(
        string targetDir, HoneConfig config, CancellationToken ct)
    {
        if (_hookDispatcher is null || _targetConfig is null)
        {
            return new HookResult(
                Success: true,
                Message: "Lifecycle hooks not configured — skipping prepare",
                Duration: TimeSpan.Zero,
                Artifacts: [],
                BaseUrl: null);
        }

        ResolvedHook hook = HookResolver.Resolve("Prepare", _targetConfig);
        var context = new HookContext(targetDir, config, BaseUrl: null, Experiment: 0);
        return await _hookDispatcher.DispatchAsync("Prepare", hook, context, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a callback that triggers server-side GC via the configured GcEndpoint.
    /// Returns null if no HttpClient or GcEndpoint is configured.
    /// </summary>
    private Func<CancellationToken, Task>? BuildCooldownCallback()
    {
        if (_httpClient is null || string.IsNullOrEmpty(_config.Api.GcEndpoint))
        {
            return null;
        }

        var gcUri = new Uri(new Uri(_config.Api.BaseUrl), _config.Api.GcEndpoint);

        return async ct =>
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Post, gcUri);
                using HttpResponseMessage response = await _httpClient.SendAsync(request, ct)
                    .ConfigureAwait(false);
                // Best-effort: ignore failures (endpoint may not exist on all targets)
            }
            catch (HttpRequestException)
            {
                // Swallow — GC endpoint is optional
            }
        };
    }
}
