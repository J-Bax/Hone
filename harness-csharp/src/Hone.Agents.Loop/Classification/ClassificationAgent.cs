using System.Globalization;

using Hone.Agents.Core;
using Hone.Core.Models;

namespace Hone.Agents.Loop.Classification;

/// <summary>
/// Invokes the hone-classifier AI agent to determine whether a proposed
/// optimization is narrow (single-file) or architectural (cross-cutting).
/// Ports the behaviour of the PowerShell <c>Invoke-ClassificationAgent.ps1</c>.
/// </summary>
public sealed class ClassificationAgent(AgentInvoker agentInvoker)
{
    private const string RetryPromptSuffix =
        "IMPORTANT: Respond with strict RFC 8259 JSON only. " +
        "Do not use NaN, Infinity, undefined, or any JavaScript literals. " +
        "Use null for missing numeric values.";

    /// <summary>
    /// Classifies the scope of a proposed optimization.
    /// </summary>
    public async Task<ClassificationResult> ClassifyAsync(
        string filePath,
        string explanation,
        string targetLabel,
        string? workingDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentException.ThrowIfNullOrEmpty(explanation);

        string prompt = BuildPrompt(filePath, explanation, targetLabel);

        AgentInvocationOptions options = new(
            AgentName: "hone-classifier",
            Prompt: prompt,
            ModelConfigKey: "ClassificationModel",
            DefaultModel: ModelDefaults.Classification,
            MaxRetries: 2,
            RetryPromptSuffix: RetryPromptSuffix,
            WorkingDirectory: workingDirectory);

        AgentResult<ClassificationResponse> result = await agentInvoker
            .InvokeAgentAsync<ClassificationResponse>(options, ct)
            .ConfigureAwait(false);

        if (result.ParsedResult?.Scope is not null)
        {
            OpportunityScope scope = string.Equals(result.ParsedResult.Scope, "narrow", StringComparison.OrdinalIgnoreCase)
                ? OpportunityScope.Narrow
                : OpportunityScope.Architecture;

            return new ClassificationResult(
                Success: result.Success,
                Scope: scope,
                Reasoning: result.ParsedResult.Reasoning,
                Response: result.RawOutput);
        }

        // Classification failed — default to architecture (safe fallback)
        return new ClassificationResult(
            Success: false,
            Scope: OpportunityScope.Architecture,
            Reasoning: string.Format(CultureInfo.InvariantCulture, "Classification failed: {0}", result.RawOutput),
            Response: result.RawOutput);
    }

    private static string BuildPrompt(string filePath, string explanation, string targetLabel)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            """
            Classify the scope of this proposed optimization.

            ## Target File
            {0}

            ## Proposed Optimization
            {1}

            Read the target file at the path above (relative to the {2} root) to verify the change can be contained to a single file.

            Respond with JSON only. No markdown, no code blocks around the JSON.
            """,
            filePath,
            explanation,
            targetLabel);
    }

    private sealed class ClassificationResponse
    {
        public string? Scope { get; set; }

        public string? Reasoning { get; set; }
    }
}
