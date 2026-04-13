using System.Globalization;
using System.Text;

using Hone.Agents.Core;

namespace Hone.Agents.Loop.Critic;

/// <summary>
/// Invokes the hone-critic AI agent to review an optimization diff
/// for correctness, scope adherence, and performance risk before
/// proceeding to the expensive load-test measurement phase.
/// </summary>
public sealed class CriticAgent(AgentInvoker agentInvoker)
{
    private const string RetryPromptSuffix =
        "IMPORTANT: Respond with strict RFC 8259 JSON only. " +
        "Do not use NaN, Infinity, undefined, or any JavaScript literals. " +
        "Use null for missing values.";

    /// <summary>
    /// Reviews the optimization diff and returns a structured verdict.
    /// </summary>
    public async Task<CriticResult> ReviewAsync(
        string filePath,
        string explanation,
        string diff,
        string? classificationScope,
        string targetLabel,
        string? workingDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentException.ThrowIfNullOrEmpty(explanation);

        string prompt = BuildPrompt(filePath, explanation, diff, classificationScope, targetLabel);

        AgentInvocationOptions options = new(
            AgentName: "hone-critic",
            Prompt: prompt,
            ModelConfigKey: "CriticModel",
            DefaultModel: ModelDefaults.Critic,
            MaxRetries: 1,
            RetryPromptSuffix: RetryPromptSuffix,
            WorkingDirectory: workingDirectory);

        AgentResult<CriticResponse> result = await agentInvoker
            .InvokeAgentAsync<CriticResponse>(options, ct)
            .ConfigureAwait(false);

        if (!result.Success || result.ParsedResult is null)
        {
            return new CriticResult(
                Success: false,
                Approved: false,
                Verdict: null,
                Confidence: null,
                Issues: null,
                Feedback: null,
                Summary: "Critic agent failed to return a valid response.",
                Response: result.RawOutput);
        }

        CriticResponse parsed = result.ParsedResult;
        string? rawVerdict = parsed.Verdict?.Trim();
        bool approved = string.Equals(rawVerdict, "APPROVE", StringComparison.OrdinalIgnoreCase);
        bool rejected = string.Equals(rawVerdict, "REJECT", StringComparison.OrdinalIgnoreCase);
        string verdict = approved ? "APPROVE" : "REJECT";

        IReadOnlyList<CriticIssue> issues = MapIssues(parsed.Issues);
        string? feedback = FormatBlockingFeedback(issues);
        string? summary = parsed.Summary;

        if (!approved && !rejected)
        {
            string invalidVerdictDetail = string.IsNullOrWhiteSpace(rawVerdict)
                ? "Critic response did not include a valid verdict; treating response as REJECT."
                : $"Critic response used invalid verdict '{rawVerdict}'; treating response as REJECT.";
            summary = string.IsNullOrWhiteSpace(summary)
                ? invalidVerdictDetail
                : $"{invalidVerdictDetail} {summary}";
        }

        return new CriticResult(
            Success: true,
            Approved: approved,
            Verdict: verdict,
            Confidence: parsed.Confidence,
            Issues: issues,
            Feedback: feedback,
            Summary: summary,
            Response: result.RawOutput);
    }

    private static string BuildPrompt(
        string filePath,
        string explanation,
        string diff,
        string? classificationScope,
        string targetLabel)
    {
        StringBuilder sb = new();

        sb.AppendLine("Review this optimization diff and determine if it should proceed to load testing.");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Target Project: {targetLabel}");
        sb.AppendLine();
        sb.AppendLine("## Target File");
        sb.AppendLine(filePath);
        sb.AppendLine();
        sb.AppendLine("## Optimization Goal");
        sb.AppendLine(explanation);
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Scope Classification: {classificationScope ?? "unknown"}");
        sb.AppendLine();
        sb.AppendLine("## Diff");
        sb.AppendLine("```diff");
        sb.AppendLine(diff);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Read the full file at the path above for context, then evaluate the diff against the review criteria.");
        sb.AppendLine("Respond with JSON only. No markdown, no code blocks around the JSON.");

        return sb.ToString();
    }

    private static IReadOnlyList<CriticIssue> MapIssues(IReadOnlyList<CriticIssueResponse>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            return [];
        }

        return [.. raw.Select(r => new CriticIssue(r.Severity, r.Category, r.Description, r.Suggestion))];
    }

    private static string? FormatBlockingFeedback(IReadOnlyList<CriticIssue> issues)
    {
        IReadOnlyList<CriticIssue> blocking = [.. issues.Where(i =>
            string.Equals(i.Severity, "blocking", StringComparison.OrdinalIgnoreCase)),];

        if (blocking.Count == 0)
        {
            return null;
        }

        return string.Join("\n\n", blocking.Select(i =>
            $"[{i.Category}] {i.Description}\nSuggestion: {i.Suggestion}"));
    }

    // ── Response DTOs for JSON deserialization ───────────────────────────────

    private sealed class CriticResponse
    {
        public string? Verdict { get; set; }
        public string? Confidence { get; set; }
        public IReadOnlyList<CriticIssueResponse>? Issues { get; set; }
        public string? Summary { get; set; }
    }

    private sealed class CriticIssueResponse
    {
        public string? Severity { get; set; }
        public string? Category { get; set; }
        public string? Description { get; set; }
        public string? Suggestion { get; set; }
    }
}
