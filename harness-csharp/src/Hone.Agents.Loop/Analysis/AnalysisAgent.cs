using System.Globalization;
using System.Text;
using System.Text.Json;

using Hone.Agents.Core;
using Hone.Core.Models;

namespace Hone.Agents.Loop.Analysis;

/// <summary>
/// Invokes the hone-analyst AI agent to identify optimization opportunities.
/// </summary>
public sealed class AnalysisAgent(AgentInvoker agentInvoker)
{
    /// <summary>
    /// Analyzes current performance metrics against the baseline and returns
    /// ranked optimization opportunities.
    /// </summary>
    public async Task<AnalysisResult> AnalyzeAsync(
        AnalysisContext context,
        MetricSet currentMetrics,
        MetricSet baselineMetrics,
        ComparisonResult? comparison,
        int experiment,
        string targetLabel,
        string? workingDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(currentMetrics);
        ArgumentNullException.ThrowIfNull(baselineMetrics);

        string prompt = BuildPrompt(context, currentMetrics, baselineMetrics, comparison, experiment, targetLabel);

        AgentInvocationOptions options = new(
            AgentName: "hone-analyst",
            Prompt: prompt,
            ModelConfigKey: "AnalysisModel",
            DefaultModel: ModelDefaults.Analysis,
            WorkingDirectory: workingDirectory);

        AgentResult<AnalysisAgentResponse> result = await agentInvoker
            .InvokeAgentAsync<AnalysisAgentResponse>(options, ct)
            .ConfigureAwait(false);

        if (result.ParsedResult?.Opportunities is { Count: > 0 })
        {
            List<Opportunity> opportunities = NormalizeOpportunities(
                result.ParsedResult.Opportunities,
                context.SourceFilePaths);
            Opportunity primary = opportunities[0];

            return new AnalysisResult(
                Success: !string.IsNullOrEmpty(primary.FilePath),
                FilePath: primary.FilePath,
                Explanation: primary.Explanation ?? primary.Title,
                Opportunities: opportunities,
                Prompt: prompt,
                Response: result.RawOutput);
        }

        return new AnalysisResult(
            Success: false,
            FilePath: null,
            Explanation: null,
            Opportunities: [],
            Prompt: prompt,
            Response: result.RawOutput);
    }

    private static string BuildPrompt(
        AnalysisContext context,
        MetricSet currentMetrics,
        MetricSet baselineMetrics,
        ComparisonResult? comparison,
        int experiment,
        string targetLabel)
    {
        string improvementPct = comparison?.ImprovementPct.ToString(CultureInfo.InvariantCulture) ?? "0";

        string fileList = context.SourceFilePaths.Count > 0
            ? string.Join('\n', context.SourceFilePaths.Select(p => $"- {p}"))
            : string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Analyze this target project's performance and identify 1-3 optimization opportunities ranked by expected impact. For each, provide a detailed root-cause analysis with evidence (code snippets + line references, not full files), theory, proposed fixes, and expected impact.");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Current Performance (Experiment {experiment})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- p95 Latency: {currentMetrics.HttpReqDuration.P95}ms");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Requests/sec: {Math.Round(currentMetrics.HttpReqs.Rate, 1).ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Error rate: {Math.Round(currentMetrics.HttpReqFailed.Rate * 100, 2).ToString(CultureInfo.InvariantCulture)}%");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Improvement vs baseline: {improvementPct}%");
        sb.AppendLine();
        sb.AppendLine("## Baseline Performance");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- p95 Latency: {baselineMetrics.HttpReqDuration.P95}ms");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Requests/sec: {Math.Round(baselineMetrics.HttpReqs.Rate, 1).ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Error rate: {Math.Round(baselineMetrics.HttpReqFailed.Rate * 100, 2).ToString(CultureInfo.InvariantCulture)}%");

        if (!string.IsNullOrEmpty(context.CounterContext))
        {
            sb.AppendLine(context.CounterContext);
        }

        if (!string.IsNullOrEmpty(context.TrafficContext))
        {
            sb.AppendLine(context.TrafficContext);
        }

        if (!string.IsNullOrEmpty(context.HistoryContext))
        {
            sb.AppendLine(context.HistoryContext);
        }

        if (!string.IsNullOrEmpty(context.ProfilingContext))
        {
            sb.AppendLine(context.ProfilingContext);
        }

        sb.AppendLine();
        sb.AppendLine("## Source Files");
        sb.AppendLine(CultureInfo.InvariantCulture, $"The following source files are available for analysis (paths relative to the {targetLabel} root).");
        sb.AppendLine("Read the files that are relevant to identifying performance bottlenecks.");
        sb.AppendLine("Each opportunity.filePath must exactly match one path from the Source Files list.");
        sb.AppendLine();
        sb.AppendLine(fileList);
        sb.AppendLine();
        sb.Append("Respond with JSON only. No markdown, no code blocks around the JSON.");

        return sb.ToString();
    }

    private static List<Opportunity> NormalizeOpportunities(
        List<OpportunityDto> dtos,
        IReadOnlyList<string> sourceFilePaths)
    {
        var result = new List<Opportunity>(dtos.Count);

        foreach (OpportunityDto dto in dtos)
        {
            if (string.IsNullOrEmpty(dto.FilePath))
            {
                throw new InvalidOperationException(
                    "Analysis agent returned an opportunity with no FilePath. " +
                    $"Title: '{dto.Title}', Explanation: '{dto.Explanation}'.");
            }

            if (string.IsNullOrEmpty(dto.Title) && string.IsNullOrEmpty(dto.Explanation))
            {
                throw new InvalidOperationException(
                    "Analysis agent returned an opportunity with neither Title nor Explanation. " +
                    $"FilePath: '{dto.FilePath}'.");
            }

            if (!Enum.TryParse(dto.Scope, ignoreCase: true, out OpportunityScope scope))
            {
                throw new InvalidOperationException(
                    $"Analysis agent returned an opportunity with invalid Scope '{dto.Scope}'. " +
                    $"FilePath: '{dto.FilePath}', Title: '{dto.Title}'. " +
                    "Expected 'Narrow' or 'Architecture'.");
            }

            string title = dto.Title ?? dto.Explanation!;
            string explanation = dto.Explanation ?? dto.Title!;
            string filePath = CanonicalizeFilePath(dto.FilePath, sourceFilePaths);

            result.Add(new Opportunity(
                FilePath: filePath,
                Title: title,
                Explanation: explanation,
                Scope: scope,
                RootCause: dto.RootCause,
                ImpactEstimate: dto.ImpactEstimate?.ValueKind == JsonValueKind.String
                    ? dto.ImpactEstimate.Value.GetString()
                    : dto.ImpactEstimate?.GetRawText()));
        }

        return result;
    }

    private static string CanonicalizeFilePath(
        string filePath,
        IReadOnlyList<string> sourceFilePaths)
    {
        string normalizedPath = NormalizeFilePath(filePath);
        if (sourceFilePaths.Count == 0)
        {
            return normalizedPath;
        }

        Dictionary<string, string> canonicalPaths = BuildCanonicalPathIndex(sourceFilePaths);
        if (canonicalPaths.TryGetValue(normalizedPath, out string? canonicalPath))
        {
            return canonicalPath;
        }

        string[] suffixMatches =
        [
            .. canonicalPaths.Values
                .Where(path => IsSuffixMatch(path, normalizedPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
        ];

        return suffixMatches.Length switch
        {
            1 => suffixMatches[0],
            > 1 => throw new InvalidOperationException(
                $"Analysis agent returned ambiguous FilePath '{filePath}'. " +
                $"It matched multiple discovered source files: {string.Join(", ", suffixMatches.Select(path => $"'{path}'"))}. " +
                "The filePath must exactly match one listed source file."),
            _ => throw new InvalidOperationException(
                $"Analysis agent returned FilePath '{filePath}', which does not match any discovered source file. " +
                "The filePath must be target-root relative and match one listed source file."),
        };
    }

    private static Dictionary<string, string> BuildCanonicalPathIndex(
        IReadOnlyList<string> sourceFilePaths)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in sourceFilePaths)
        {
            string normalized = NormalizeFilePath(path);
            if (!string.IsNullOrEmpty(normalized))
            {
                index[normalized] = normalized;
            }
        }

        return index;
    }

    private static bool IsSuffixMatch(string path, string suffix) =>
        string.Equals(path, suffix, StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith($"/{suffix}", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeFilePath(string path)
    {
        string normalized = path.Replace('\\', '/').Trim();

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.Trim('/');
    }

    private sealed class AnalysisAgentResponse
    {
        public List<OpportunityDto>? Opportunities { get; set; }
    }

    private sealed class OpportunityDto
    {
        public string? FilePath { get; set; }

        public string? Title { get; set; }

        public string? Explanation { get; set; }

        public string? Scope { get; set; }

        public string? RootCause { get; set; }

        public JsonElement? ImpactEstimate { get; set; }
    }
}
