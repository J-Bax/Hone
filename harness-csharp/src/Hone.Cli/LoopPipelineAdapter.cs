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
        string targetDir, HoneConfig config, Uri? baseUrl, CancellationToken ct)
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
        Directory.CreateDirectory(outputDir);
        Uri measurementBaseUrl = ResolveMeasurementBaseUrl(config, baseUrl);

        ScaleTestResult result = await ScaleTestOrchestrator.RunAsync(
            config.ScaleTest,
            _loadTestRunner,
            measurementBaseUrl,
            outputDir,
            experiment: 0,
            afterRunCallback: BuildCooldownHookCallback(targetDir, measurementBaseUrl, experiment: 0),
            ct: ct,
            workingDirectory: targetDir).ConfigureAwait(false);

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
        Uri measurementBaseUrl = ResolveMeasurementBaseUrl(_config, input.BaseUrl);

        // Dispatch Warmup hook before the scale test measurement phase
        _ = await WarmupAsync(input.TargetDir, _config, measurementBaseUrl, input.Experiment, ct)
            .ConfigureAwait(false);

        string outputDir = Path.Combine(input.TargetDir, input.ResultsPath, $"experiment-{input.Experiment}");

        // Use Cooldown hook dispatch as the after-run callback (replaces hardcoded GC endpoint)
        Func<CancellationToken, Task>? afterRunCallback = BuildCooldownHookCallback(
            input.TargetDir, measurementBaseUrl, input.Experiment);

        ScaleTestResult result = await ScaleTestOrchestrator.RunAsync(
            _config.ScaleTest,
            _loadTestRunner,
            measurementBaseUrl,
            outputDir,
            input.Experiment,
            afterRunCallback: afterRunCallback,
            ct: ct,
            workingDirectory: input.TargetDir).ConfigureAwait(false);

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
        await AtomicFileWriter.WriteJsonAsync(path, metadata, MetadataJsonOptions, ct).ConfigureAwait(false);
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

    /// <inheritdoc />
    public async Task<HookResult> WarmupAsync(
        string targetDir, HoneConfig config, Uri? baseUrl, int experiment, CancellationToken ct)
    {
        return await DispatchLifecycleHookAsync("Warmup", targetDir, config, baseUrl, experiment, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HookResult> CooldownAsync(
        string targetDir, HoneConfig config, Uri? baseUrl, int experiment, CancellationToken ct)
    {
        return await DispatchLifecycleHookAsync("Cooldown", targetDir, config, baseUrl, experiment, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HookResult> CleanupAsync(
        string targetDir, HoneConfig config, CancellationToken ct)
    {
        if (_hookDispatcher is null || _targetConfig is null)
        {
            return new HookResult(
                Success: true,
                Message: "Lifecycle hooks not configured — skipping cleanup",
                Duration: TimeSpan.Zero,
                Artifacts: [],
                BaseUrl: null);
        }

        ResolvedHook hook = HookResolver.Resolve("Cleanup", _targetConfig);
        var context = new HookContext(targetDir, config, BaseUrl: null, Experiment: 0);
        return await _hookDispatcher.DispatchAsync("Cleanup", hook, context, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a callback that dispatches the Cooldown lifecycle hook after each k6 run.
    /// When hooks are configured, the Cooldown hook handles inter-run cleanup (e.g., GC trigger).
    /// Falls back to the legacy GC endpoint callback when hooks are not configured.
    /// </summary>
    private Func<CancellationToken, Task>? BuildCooldownHookCallback(string targetDir, Uri? baseUrl, int experiment)
    {
        if (_hookDispatcher is not null && _targetConfig is not null)
        {
            return async ct =>
                _ = await CooldownAsync(targetDir, _config, baseUrl, experiment, ct).ConfigureAwait(false);
        }

        // Fallback: legacy hardcoded GC endpoint callback
        if (_httpClient is null || string.IsNullOrEmpty(_config.Api.GcEndpoint))
        {
            return null;
        }

        Uri? gcBaseUrl = TryGetContextBaseUrl(_config, baseUrl);
        if (gcBaseUrl is null)
        {
            return null;
        }

        var gcUri = new Uri(gcBaseUrl, _config.Api.GcEndpoint);

        return async ct =>
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Post, gcUri);
                using HttpResponseMessage response = await _httpClient.SendAsync(request, ct)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // Swallow — GC endpoint is optional
            }
        };
    }

    private async Task<HookResult> DispatchLifecycleHookAsync(
        string hookName,
        string targetDir,
        HoneConfig config,
        Uri? baseUrl,
        int experiment,
        CancellationToken ct)
    {
        if (_hookDispatcher is null || _targetConfig is null)
        {
            return new HookResult(
                Success: true,
                Message: $"Lifecycle hooks not configured — skipping {hookName}",
                Duration: TimeSpan.Zero,
                Artifacts: [],
                BaseUrl: null);
        }

        ResolvedHook hook = HookResolver.Resolve(hookName, _targetConfig);
        var context = new HookContext(
            targetDir,
            config,
            BaseUrl: TryGetContextBaseUrl(config, baseUrl),
            experiment);

        return await _hookDispatcher.DispatchAsync(hookName, hook, context, ct).ConfigureAwait(false);
    }

    private static Uri ResolveMeasurementBaseUrl(HoneConfig config, Uri? runtimeBaseUrl)
    {
        return TryGetContextBaseUrl(config, runtimeBaseUrl)
            ?? throw new InvalidOperationException(
                "Load testing requires a concrete runtime BaseUrl. Start the target before running measurements when Api.BaseUrl uses port 0.");
    }

    private static Uri? TryGetContextBaseUrl(HoneConfig config, Uri? runtimeBaseUrl)
    {
        if (runtimeBaseUrl is not null)
        {
            return runtimeBaseUrl;
        }

        var configuredBaseUrl = new Uri(config.Api.BaseUrl);
        return configuredBaseUrl.Port == 0 ? null : configuredBaseUrl;
    }
}
