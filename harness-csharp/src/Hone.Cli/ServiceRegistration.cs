using Hone.Agents.CopilotCli;
using Hone.Agents.Core;
using Hone.Agents.Loop.Analysis;
using Hone.Agents.Loop.Classification;
using Hone.Agents.Loop.Critic;
using Hone.Agents.Loop.Implementer;
using Hone.Agents.Preparation;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Observability;
using Hone.Diagnostics.Discovery;
using Hone.Lifecycle.Hooks;
using Hone.Lifecycle.SharedHooks;
using Hone.Measurement.DotnetCounters;
using Hone.Measurement.K6;
using Hone.Orchestration.Failure;
using Hone.Orchestration.Implementer;
using Hone.Orchestration.Loop;
using Hone.Orchestration.Queue;
using Hone.SourceControl.Experiments;
using Hone.SourceControl.Git;
using Hone.SourceControl.PullRequests;

namespace Hone.Cli;

/// <summary>
/// Builds the DI container that wires all Hone components from Phases 1-8.
/// </summary>
internal static class ServiceRegistration
{
    /// <summary>
    /// Builds a fully configured <see cref="IServiceProvider"/> for the Hone CLI.
    /// </summary>
    internal static IServiceProvider Build(string targetDir, HoneConfig config, string configPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetDir);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrEmpty(configPath);

        string resultsPath = config.Api.ResultsPath;
        string metadataPath = config.Api.MetadataPath;
        string logPath = Path.Combine(targetDir, resultsPath, "hone.log");

        // ── Observability ────────────────────────────────────────────────
        var consoleSink = new ConsoleEventSink(Console.Out);
        var jsonLogSink = new JsonLogEventSink(logPath, config.Logging.MaxFileSizeMB * 1024L * 1024);
        var eventBus = new HoneEventBus();
        eventBus.Register(consoleSink);
        eventBus.Register(jsonLogSink);

        // ── Infrastructure ───────────────────────────────────────────────
        IProcessRunner processRunner = new ProcessRunner();
        IVersionControl versionControl = new GitVersionControl(processRunner);
        ICodeHost codeHost = new GitHubCodeHost(processRunner);

        // ── Agents ───────────────────────────────────────────────────────
        IAgentRunner agentRunner = new CopilotCliAgentRunner();
        var agentInvoker = new AgentInvoker(agentRunner, config.Agents);
        var analysisAgent = new AnalysisAgent(agentInvoker);
        var classificationAgent = new ClassificationAgent(agentInvoker);
        var implementerAgent = new ImplementerAgent(agentInvoker);
        var criticAgent = new CriticAgent(agentInvoker);
        var compatibilityAgent = new CompatibilityAgent(agentInvoker, processRunner);

        // ── Measurement ──────────────────────────────────────────────────
        ILoadTestRunner loadTestRunner = new K6LoadTestRunner(processRunner);
        IRuntimeMetricsCollector metricsCollector = new DotnetCountersCollector(processRunner);

        // ── Source control ───────────────────────────────────────────────
        var branchManager = new ExperimentBranchManager(versionControl);
        var prManager = new PullRequestManager(codeHost);

        // ── Lifecycle ────────────────────────────────────────────────────
#pragma warning disable CA2000 // HttpClient is intentionally process-scoped: the CLI is short-lived, only polls localhost health endpoints, and socket exhaustion / DNS staleness do not apply
        var httpClient = new HttpClient();
#pragma warning restore CA2000
        var pluginDiscovery = new PluginDiscoveryService();

        // ── Lifecycle hooks ──────────────────────────────────────────────
        TargetConfig targetConfig = TargetConfigLoader.Load(configPath);
        var dotnetBuildHook = new DotnetBuildHook(processRunner);
        var dotnetTestHook = new DotnetTestHook(processRunner);
        var dotnetStartHook = new DotnetStartHook(httpClient);
        var dotnetStopHook = new DotnetStopHook();
        var healthPollHook = new HealthPollHook(httpClient);
        var k6RunHook = new K6RunHook(loadTestRunner);
        var hookRegistry = new BuiltInHookRegistry(
            dotnetBuildHook, dotnetTestHook, dotnetStartHook,
            dotnetStopHook, healthPollHook, k6RunHook);
        var hookDispatcher = new LifecycleHookDispatcher(hookRegistry, processRunner, httpClient);

        // ── Pipeline adapters ────────────────────────────────────────────
        ILoopPipeline loopPipeline = new LoopPipelineAdapter(
            loadTestRunner,
            analysisAgent,
            classificationAgent,
            codeHost,
            versionControl,
            config,
            httpClient,
            hookDispatcher,
            targetConfig);

        IImplementerPipeline implementerPipeline = new ImplementerPipelineAdapter(
            implementerAgent,
            criticAgent,
            versionControl,
            processRunner,
            eventBus,
            config,
            hookDispatcher,
            targetConfig);

        // ── Orchestration ────────────────────────────────────────────────
        var queueManager = new OptimizationQueueManager(metadataPath, eventBus);
        var implementerRunner = new IterativeImplementerRunner(implementerPipeline, eventBus);
        var failureHandler = new ExperimentFailureHandler(versionControl, queueManager, eventBus);
        var loopRunner = new HoneLoopRunner(
            loopPipeline, queueManager, implementerRunner, failureHandler, eventBus);

        // ── Build provider (manual wiring — no container framework needed) ──
        var provider = new ManualServiceProvider();
        provider.Register<IHoneEventSink>(eventBus);
        provider.Register<HoneEventBus>(eventBus);
        provider.Register<IProcessRunner>(processRunner);
        provider.Register<IVersionControl>(versionControl);
        provider.Register<ICodeHost>(codeHost);
        provider.Register<ILoopPipeline>(loopPipeline);
        provider.Register<IAgentRunner>(agentRunner);
        provider.Register(agentInvoker);
        provider.Register(analysisAgent);
        provider.Register(classificationAgent);
        provider.Register(implementerAgent);
        provider.Register(compatibilityAgent);
        provider.Register<ILoadTestRunner>(loadTestRunner);
        provider.Register<IRuntimeMetricsCollector>(metricsCollector);
        provider.Register(branchManager);
        provider.Register(prManager);
        provider.Register(pluginDiscovery);
        provider.Register(queueManager);
        provider.Register(implementerRunner);
        provider.Register(failureHandler);
        provider.Register(loopRunner);
        provider.Register(httpClient);
        provider.Register(config);
        provider.Register(targetConfig);

        return provider;
    }

    /// <summary>
    /// Minimal service provider backed by a dictionary. Full DI containers
    /// (Microsoft.Extensions.DependencyInjection) can replace this later if needed.
    /// </summary>
    private sealed class ManualServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = [];

        public void Register<T>(T instance) where T : notnull
        {
            _services[typeof(T)] = instance;
        }

        public object? GetService(Type serviceType)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return _services.GetValueOrDefault(serviceType);
        }
    }
}
