using System.Globalization;
using System.Text.Json;

using Hone.Agents.Core;
using Hone.Core.Contracts;

namespace Hone.Agents.Preparation;

/// <summary>
/// Assesses a target project's compatibility with the Hone optimization harness.
/// </summary>
public sealed class CompatibilityAgent
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private const string DefaultModel = ModelDefaults.Compatibility;
    private const string ModelConfigKey = "AnalysisModel";
    private const string AgentName = "hone-compatibility";
    private const int MaxRetries = 1;
    private const string RetryPromptSuffix =
        "Your previous response was not valid JSON. Please respond with ONLY a JSON object matching the output schema.";

    private readonly AgentInvoker _invoker;
    private readonly IProcessRunner? _processRunner;

    public CompatibilityAgent(AgentInvoker invoker, IProcessRunner? processRunner = null)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        _invoker = invoker;
        _processRunner = processRunner;
    }

    /// <summary>
    /// Runs a full compatibility assessment against the specified target directory.
    /// </summary>
    /// <param name="targetPath">Root directory of the target project.</param>
    /// <param name="model">Optional model override.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="CompatibilityResult"/> with the assessment outcome.</returns>
    public async Task<CompatibilityResult> AssessAsync(
        string targetPath,
        string? model = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        string fullPath = Path.GetFullPath(targetPath);
        if (!Directory.Exists(fullPath))
        {
            return new CompatibilityResult(
                Success: false,
                Message: $"Target directory not found: {fullPath}",
                Report: null);
        }

        // Pre-probe
        PreProbeData preProbe = await PreProber.ProbeAsync(fullPath, _processRunner, ct)
            .ConfigureAwait(false);

        // Build prompt
        string prompt = BuildPrompt(preProbe);

        // Invoke agent
        var options = new AgentInvocationOptions(
            AgentName: AgentName,
            Prompt: prompt,
            ModelConfigKey: ModelConfigKey,
            DefaultModel: model ?? DefaultModel,
            MaxRetries: MaxRetries,
            RetryPromptSuffix: RetryPromptSuffix,
            WorkingDirectory: fullPath);

        AgentResult<CompatibilityReport> agentResult =
            await _invoker.InvokeAgentAsync<CompatibilityReport>(options, ct).ConfigureAwait(false);

        if (agentResult.TimedOut)
        {
            return new CompatibilityResult(
                Success: false,
                Message: "Agent timed out",
                Report: null);
        }

        if (!agentResult.Success || agentResult.ParsedResult is null)
        {
            return new CompatibilityResult(
                Success: false,
                Message: "Agent response was not valid JSON",
                Report: null);
        }

        CompatibilityReport report = agentResult.ParsedResult;
        string message = FormatSummaryMessage(report);

        return new CompatibilityResult(
            Success: true,
            Message: message,
            Report: report);
    }

    private static string BuildPrompt(PreProbeData preProbe)
    {
        string preProbeJson = JsonSerializer.Serialize(preProbe, SerializeOptions);

        return $"""
            Assess the following target project for Hone compatibility.

            ## Target Pre-Probe Data

            ```json
            {preProbeJson}
            ```

            ## Instructions

            1. Use the pre-probe data above as your starting point.
            2. The target project is located at: {preProbe.TargetPath}
            3. Actively investigate by reading key files and running commands.
            4. For build and test commands, run them from the target directory: {preProbe.TargetPath}
            5. Produce a complete compatibility assessment following your output schema.

            ## Source Code Path Detection

            The pre-probe data includes `detectedSourceCodePaths` — directories that were
            automatically identified as containing source files. You MUST validate and refine
            these paths:

            1. Review each detected path — confirm it contains application logic (not generated
               code, configuration-only, or third-party vendored code).
            2. Look for source directories the detector may have missed — check project file
               references, namespace declarations, and import/using statements.
            3. Remove false positives (generated code, designer files, migration-only dirs).
            4. Return the final refined list in `detectedConfig.sourceCodePaths` relative to
               the project root directory (not the solution root).
            5. Also set `detectedConfig.sourceFileGlob` to the appropriate file extension
               pattern for the detected stack (e.g., "*.cs", "*.ts", "*.go").

            ## Important

            - Run the build command to verify CLI buildability.
            - Run the test command to verify test suite health.
            - Read configuration files (appsettings.json, Program.cs, Startup.cs, etc.) to detect endpoints, health checks, database config.
            - If .hone/ already exists, validate it against the adapter contract.
            - Produce ONLY the JSON output. No other text.
            """;
    }

    private static string FormatSummaryMessage(CompatibilityReport report)
    {
        string overall = report.Compatibility?.Overall?.ToUpperInvariant() ?? "UNKNOWN";
        string score = report.Compatibility?.Score is not null
            ? string.Create(CultureInfo.InvariantCulture, $"{report.Compatibility.Score}/100")
            : "N/A";

        return $"Assessment complete: {overall} ({score})";
    }
}
