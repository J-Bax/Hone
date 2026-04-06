using System.Text.Json;

using Hone.Core.Constants;
using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Diagnostics.Analyzers;

/// <summary>
/// Analyzes GC statistics and allocation data by building a prompt with
/// GC report content and performance metrics, then calling the
/// <c>hone-memory-profiler</c> AI agent.
/// </summary>
internal sealed class MemoryGcAnalyzer : IAnalyzerPlugin
{
    private const string AgentName = "hone-memory-profiler";
    private const string PromptFileName = "memory-gc-prompt.md";
    private const string ResponseFileName = "memory-gc-response.json";

    private readonly IAgentRunner _agentRunner;

    /// <summary>
    /// Initializes a new instance of <see cref="MemoryGcAnalyzer"/>.
    /// </summary>
    public MemoryGcAnalyzer(IAgentRunner agentRunner)
    {
        ArgumentNullException.ThrowIfNull(agentRunner);
        _agentRunner = agentRunner;
    }

    /// <inheritdoc />
    public string Name => "memory-gc";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredCollectors { get; } = [WellKnownCollectors.PerfViewGc];

    /// <inheritdoc />
    public async Task<AnalyzerResult> AnalyzeAsync(
        AnalyzerContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // ── Resolve GC report data ──────────────────────────────────────
        if (!context.CollectorData.TryGetValue(WellKnownCollectors.PerfViewGc, out CollectorExportResult? gcData))
        {
            return new AnalyzerResult(
                Success: false,
                Summary: "No GC collector data available");
        }

        string? gcReportPath = AnalyzerPromptHelper.ResolveDataPath(gcData, "GcReportPath");
        string? gcReportContent = await AnalyzerPromptHelper.ReadFileOrNullAsync(gcReportPath, ct).ConfigureAwait(false);
        if (gcReportContent is null)
        {
            return new AnalyzerResult(
                Success: false,
                Summary: $"GC report file not found: {gcReportPath ?? "(no path)"}");
        }

        // ── Optionally read allocation type data from perfview-cpu ──────
        string? allocTypesContent = null;
        if (context.CollectorData.TryGetValue(WellKnownCollectors.PerfViewCpu, out CollectorExportResult? cpuData))
        {
            string? allocPath = AnalyzerPromptHelper.ResolveDataPath(cpuData, "AllocTypesPath", fallbackIndex: 1);
            allocTypesContent = await AnalyzerPromptHelper.ReadFileOrNullAsync(allocPath, ct).ConfigureAwait(false);
        }

        // ── Build prompt ────────────────────────────────────────────────
        string metricsSection = AnalyzerPromptHelper.FormatMetricsSection(context.CurrentMetrics);
        string allocSection = allocTypesContent is not null
            ? $"""

              ## Top Allocating Types (from sampled allocation ticks)

              {allocTypesContent}
              """
            : string.Empty;

        string prompt = $"""
            Analyze the following GC and memory data from PerfView. The data includes GC statistics,
            heap behavior, and allocation patterns captured during a load test.

            {metricsSection}

            ## GC and Memory Report

            {gcReportContent}
            {allocSection}
            Respond with JSON only.
            """;

        // ── Save prompt ─────────────────────────────────────────────────
        string promptPath = await AnalyzerPromptHelper.SavePromptAsync(
            context.OutputDir, PromptFileName, prompt, ct).ConfigureAwait(false);

        // ── Call agent ──────────────────────────────────────────────────
        string model = AnalyzerPromptHelper.GetModel(context.Settings);

        AgentRunResult agentResult;
        try
        {
            agentResult = await _agentRunner.InvokeAsync(
                new AgentInvocation(AgentName, prompt, Model: model),
                ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate resilience boundary
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new AnalyzerResult(
                Success: false,
                Summary: $"Analysis agent error: {ex.Message}",
                PromptPath: promptPath);
        }

        // ── Save response ───────────────────────────────────────────────
        string responsePath = await AnalyzerPromptHelper.SaveResponseAsync(
            context.OutputDir, ResponseFileName, agentResult.Output, ct).ConfigureAwait(false);

        // ── Parse JSON report ───────────────────────────────────────────
        JsonElement? report = AnalyzerPromptHelper.ParseJsonReport(agentResult.Output);
        string summary = AnalyzerPromptHelper.ExtractSummary(report)
            ?? "Analysis complete — no summary provided by agent.";

        return new AnalyzerResult(
            Success: agentResult.Success && report is not null,
            Report: report,
            Summary: summary,
            PromptPath: promptPath,
            ResponsePath: responsePath);
    }
}
