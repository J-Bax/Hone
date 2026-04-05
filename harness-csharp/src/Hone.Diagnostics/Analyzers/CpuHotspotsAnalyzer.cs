using System.Text.Json;

using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Diagnostics.Analyzers;

/// <summary>
/// Analyzes CPU sampling stacks by building a prompt with folded-stack data
/// and performance metrics, then calling the <c>hone-cpu-profiler</c> AI agent.
/// Ports <c>harness/analyzers/cpu-hotspots/Invoke-Analyzer.ps1</c>.
/// </summary>
internal sealed class CpuHotspotsAnalyzer : IAnalyzerPlugin
{
    private const string AgentName = "hone-cpu-profiler";
    private const string PromptFileName = "cpu-hotspots-prompt.md";
    private const string ResponseFileName = "cpu-hotspots-response.json";

    private readonly IAgentRunner _agentRunner;

    /// <summary>
    /// Initializes a new instance of <see cref="CpuHotspotsAnalyzer"/>.
    /// </summary>
    public CpuHotspotsAnalyzer(IAgentRunner agentRunner)
    {
        ArgumentNullException.ThrowIfNull(agentRunner);
        _agentRunner = agentRunner;
    }

    /// <inheritdoc />
    public string Name => "cpu-hotspots";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredCollectors { get; } = ["perfview-cpu"];

    /// <inheritdoc />
    public async Task<AnalyzerResult> AnalyzeAsync(
        AnalyzerContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // ── Resolve collector data ──────────────────────────────────────
        if (!context.CollectorData.TryGetValue("perfview-cpu", out CollectorExportResult? perfviewData))
        {
            return new AnalyzerResult(
                Success: false,
                Summary: "No CPU collector data available");
        }

        string? stacksPath = AnalyzerPromptHelper.ResolveDataPath(perfviewData, "CpuStacksPath");
        string? stacksContent = await AnalyzerPromptHelper.ReadFileOrNullAsync(stacksPath, ct).ConfigureAwait(false);
        if (stacksContent is null)
        {
            return new AnalyzerResult(
                Success: false,
                Summary: $"Folded stacks file not found: {stacksPath ?? "(no path)"}");
        }

        // ── Truncate to top N stacks ────────────────────────────────────
        int maxStacks = AnalyzerPromptHelper.GetMaxStacks(context.Settings);
        string truncatedStacks = AnalyzerPromptHelper.TruncateStacks(stacksContent, maxStacks);
        int stackCount = string.IsNullOrEmpty(truncatedStacks)
            ? 0
            : truncatedStacks.Split('\n').Length;

        // ── Build prompt ────────────────────────────────────────────────
        string metricsSection = AnalyzerPromptHelper.FormatMetricsSection(context.CurrentMetrics);
        string prompt = $"""
            Analyze the following CPU sampling data from PerfView. The data is in folded stack format
            (call chain separated by semicolons, followed by sample count).

            {metricsSection}

            ## CPU Sampling Data (top {stackCount} stacks by sample count)

            {truncatedStacks}

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
