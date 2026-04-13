using System.Text.Json;
using System.Text.Json.Serialization;

using Hone.Agents.Core;

namespace Hone.Agents.Preparation;

/// <summary>
/// Files and notes produced by the hone-scaffolder agent.
/// </summary>
public sealed record ScaffoldPlan
{
    [JsonPropertyName("files")]
    public IReadOnlyDictionary<string, string>? Files { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

/// <summary>
/// Result returned by <see cref="ScaffolderAgent.ScaffoldAsync"/>.
/// </summary>
public sealed record ScaffoldResult(
    bool Success,
    string Message,
    ScaffoldPlan? Plan);

/// <summary>
/// Invokes the hone-scaffolder agent to generate .hone/ configuration files
/// from a completed compatibility assessment.
/// </summary>
public sealed class ScaffolderAgent
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string AgentName = "hone-scaffolder";
    private const string DefaultModel = ModelDefaults.Scaffolder;
    private const int MaxRetries = 1;
    private const string RetryPromptSuffix =
        "Your previous response was not valid JSON. Please respond with ONLY a JSON object matching the output schema.";

    private readonly AgentInvoker _invoker;

    public ScaffolderAgent(AgentInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        _invoker = invoker;
    }

    /// <summary>
    /// Generates a scaffold plan containing .hone/ configuration files.
    /// </summary>
    /// <param name="report">The compatibility assessment report.</param>
    /// <param name="preProbe">Pre-probe data from the target directory.</param>
    /// <param name="model">Optional model override.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ScaffoldResult"/> with the generated file plan.</returns>
    public async Task<ScaffoldResult> ScaffoldAsync(
        CompatibilityReport report,
        PreProbeData preProbe,
        string? model = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(preProbe);

        string prompt = BuildPrompt(report, preProbe);

        var options = new AgentInvocationOptions(
            AgentName: AgentName,
            Prompt: prompt,
            DefaultModel: model ?? DefaultModel,
            MaxRetries: MaxRetries,
            RetryPromptSuffix: RetryPromptSuffix,
            WorkingDirectory: preProbe.TargetPath);

        AgentResult<ScaffoldPlan> agentResult =
            await _invoker.InvokeAgentAsync<ScaffoldPlan>(options, ct).ConfigureAwait(false);

        if (agentResult.TimedOut)
        {
            return new ScaffoldResult(
                Success: false,
                Message: "Agent timed out",
                Plan: null);
        }

        if (!agentResult.Success || agentResult.ParsedResult is null)
        {
            return new ScaffoldResult(
                Success: false,
                Message: "Agent response was not valid JSON",
                Plan: null);
        }

        ScaffoldPlan plan = agentResult.ParsedResult;

        if (plan.Files is null || plan.Files.Count == 0)
        {
            return new ScaffoldResult(
                Success: false,
                Message: "Agent produced an empty scaffold plan",
                Plan: plan);
        }

        return new ScaffoldResult(
            Success: true,
            Message: $"Scaffold plan generated with {plan.Files.Count} file(s)",
            Plan: plan);
    }

    private static string BuildPrompt(CompatibilityReport report, PreProbeData preProbe)
    {
        string reportJson = JsonSerializer.Serialize(report, SerializeOptions);
        string preProbeJson = JsonSerializer.Serialize(preProbe, SerializeOptions);

        return $"""
            Generate .hone/ configuration files for the following target project.

            ## Compatibility Assessment Report

            ```json
            {reportJson}
            ```

            ## Pre-Probe Data

            ```json
            {preProbeJson}
            ```

            ## Instructions

            1. Use the assessment report to determine the correct hooks, config values, and k6 scenarios.
            2. Generate a complete `.hone/config.yaml` with PascalCase keys.
            3. Generate k6 scenario file(s) targeting the detected API endpoints.
            4. Generate any hook scripts needed for Command-type hooks.
            5. Prefer BuiltIn hooks for .NET projects where shared hooks exist.
            6. Use `http://localhost:0` as the BaseUrl for ephemeral port assignment.
            7. Produce ONLY the JSON output matching the output schema. No other text.
            """;
    }
}
