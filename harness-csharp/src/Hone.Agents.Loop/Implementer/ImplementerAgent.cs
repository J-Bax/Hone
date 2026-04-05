using System.Globalization;
using System.Text;

using Hone.Agents.Core;
using Hone.Core.Utilities;

namespace Hone.Agents.Loop.Implementer;

/// <summary>
/// Invokes the hone-fixer AI agent to generate optimized file content.
/// Ports the behaviour of the PowerShell <c>Invoke-FixAgent.ps1</c>.
/// </summary>
public sealed class ImplementerAgent(AgentInvoker agentInvoker)
{
    /// <summary>
    /// Generates optimized file content for the given target file.
    /// </summary>
    public async Task<ImplementerResult> ImplementAsync(
        string filePath,
        string explanation,
        string targetLabel,
        string? workingDirectory,
        int attempt = 1,
        string? previousErrors = null,
        string? currentFileContent = null,
        string? rootCauseDocument = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentException.ThrowIfNullOrEmpty(explanation);

        string prompt = BuildPrompt(filePath, explanation, targetLabel, attempt, previousErrors, currentFileContent, rootCauseDocument);

        AgentInvocationOptions options = new(
            AgentName: "hone-fixer",
            Prompt: prompt,
            ModelConfigKey: "FixModel",
            DefaultModel: "claude-opus-4.6",
            WorkingDirectory: workingDirectory);

        // The fixer returns code, not JSON. We use InvokeAgentAsync<object> to
        // get model resolution and timeout handling, then extract the code block
        // from RawOutput ourselves.
        AgentResult<object> result = await agentInvoker
            .InvokeAgentAsync<object>(options, ct)
            .ConfigureAwait(false);

        string? codeBlock = ExtractCodeBlockOrNull(result.RawOutput);

        return new ImplementerResult(
            Success: result.ExitCode == 0 && codeBlock is not null,
            CodeBlock: codeBlock,
            Response: result.RawOutput,
            Attempt: attempt);
    }

    /// <summary>
    /// Extracts a fenced code block from the response, or returns <c>null</c>
    /// when the response does not contain triple-backtick fences.
    /// </summary>
    private static string? ExtractCodeBlockOrNull(string response)
    {
        if (!response.Contains("```", StringComparison.Ordinal))
        {
            return null;
        }

        string extracted = JsonUtils.ExtractCodeBlock(response);

        // ExtractCodeBlock returns the original text when no fences match.
        // Since we already checked for ```, this should not happen, but guard anyway.
        if (ReferenceEquals(extracted, response))
        {
            return null;
        }

        return extracted;
    }

    private static string BuildPrompt(
        string filePath,
        string explanation,
        string targetLabel,
        int attempt,
        string? previousErrors,
        string? currentFileContent,
        string? rootCauseDocument)
    {
        StringBuilder sb = new();

        sb.AppendLine("Apply this specific optimization to the file and return the complete new file content.");
        sb.AppendLine();
        sb.AppendLine("## Target File");
        sb.AppendLine(filePath);
        sb.AppendLine();
        sb.AppendLine("## Optimization to Apply");
        sb.AppendLine(explanation);

        if (rootCauseDocument is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Root Cause Analysis");
            sb.AppendLine();
            sb.AppendLine(rootCauseDocument);
        }

        if (attempt > 1 || previousErrors is not null || currentFileContent is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Retry Context");
            sb.AppendLine(CultureInfo.InvariantCulture, $"This is attempt {attempt} for the same optimization. The previous attempt failed.");
            sb.AppendLine();
            sb.AppendLine("### Error Output");
            sb.AppendLine("```text");
            sb.AppendLine(previousErrors ?? "Previous attempt failed without a captured error payload.");
            sb.AppendLine("```");

            if (currentFileContent is not null)
            {
                sb.AppendLine();
                sb.AppendLine("### Current File Content (failed attempt)");
                sb.AppendLine("```text");
                sb.AppendLine(currentFileContent);
                sb.AppendLine("```");
            }

            sb.AppendLine("Fix the failure above while still achieving the original optimization goal.");
        }

        sb.AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"Read the file at the path above (relative to the {targetLabel} root), apply ONLY the");
        sb.AppendLine();
        sb.AppendLine("optimization described, and return the COMPLETE new file in a fenced code block.");
        sb.AppendLine("No explanation, no commentary — just the code block.");

        return sb.ToString();
    }
}
