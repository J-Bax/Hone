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
    private const string AgentRelativePath = ".github/agents/hone-compatibility.agent.md";
    private const int MaxRetries = 2;
    private const string RetryPromptSuffix =
        """
        Your previous response was not valid JSON.
        Do not re-run the investigation or add narration.
        Repair the previous response into ONLY the final JSON object matching the required schema.
        Do NOT include any prefixed chatter, progress narration, markdown fences, summaries, or trailing notes.
        The first non-whitespace character of your response must be '{' and the last non-whitespace character must be '}'.
        """;

    private readonly AgentInvoker _invoker;
    private readonly IProcessRunner? _processRunner;
    private readonly Func<string?> _agentWorkingDirectoryResolver;

    public CompatibilityAgent(AgentInvoker invoker, IProcessRunner? processRunner = null)
        : this(invoker, processRunner, ResolveAgentWorkingDirectory)
    {
    }

    internal CompatibilityAgent(
        AgentInvoker invoker,
        IProcessRunner? processRunner,
        Func<string?> agentWorkingDirectoryResolver)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        ArgumentNullException.ThrowIfNull(agentWorkingDirectoryResolver);

        _invoker = invoker;
        _processRunner = processRunner;
        _agentWorkingDirectoryResolver = agentWorkingDirectoryResolver;
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
                Report: null,
                PreProbe: null);
        }

        string? agentWorkingDirectory = _agentWorkingDirectoryResolver();
        if (string.IsNullOrWhiteSpace(agentWorkingDirectory))
        {
            return new CompatibilityResult(
                Success: false,
                Message: $"Could not locate {AgentRelativePath} for the {AgentName} agent.",
                Report: null,
                PreProbe: null);
        }

        // Pre-probe
        PreProbeData preProbe = await PreProber.ProbeAsync(fullPath, _processRunner, ct)
            .ConfigureAwait(false);

        // Build prompt
        string prompt = BuildPrompt(preProbe);

        // Invoke agent from the Hone repo so the custom agent remains discoverable.
        // The prompt carries the absolute target path and we explicitly allow that directory.
        var options = new AgentInvocationOptions(
            AgentName: AgentName,
            Prompt: prompt,
            ModelConfigKey: ModelConfigKey,
            DefaultModel: model ?? DefaultModel,
            MaxRetries: MaxRetries,
            RetryPromptSuffix: RetryPromptSuffix,
            WorkingDirectory: agentWorkingDirectory,
            AdditionalAllowedDirectories: [fullPath],
            IncludePreviousOutputInRetryPrompt: true);

        AgentResult<CompatibilityReport> agentResult =
            await _invoker.InvokeAgentAsync<CompatibilityReport>(options, ct).ConfigureAwait(false);

        if (agentResult.TimedOut)
        {
            return new CompatibilityResult(
                Success: false,
                Message: "Agent timed out",
                Report: null,
                PreProbe: preProbe);
        }

        if (!agentResult.Success || agentResult.ParsedResult is null)
        {
            return new CompatibilityResult(
                Success: false,
                Message: GetFailureMessage(agentResult.RawOutput),
                Report: null,
                PreProbe: preProbe);
        }

        CompatibilityReport report = agentResult.ParsedResult;
        string message = FormatSummaryMessage(report);

        return new CompatibilityResult(
            Success: true,
            Message: message,
            Report: report,
            PreProbe: preProbe);
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
            6. Your current working directory may not start in the target project. Change into {preProbe.TargetPath} before reading files or running shell commands against the target, or use absolute paths within that directory.

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
            
            ## Final Output Contract

            - Produce ONLY the final JSON object. No other text.
            - Do NOT narrate your investigation, planning, or progress.
            - Do NOT use markdown fences.
            - The first non-whitespace character of your response must be an opening curly brace.
            - The last non-whitespace character of your response must be a closing curly brace.
            - Any text before or after the JSON object will be treated as a failure and trigger a retry.
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

    private static string GetFailureMessage(string rawOutput)
    {
        if (!string.IsNullOrWhiteSpace(rawOutput)
            && rawOutput.Contains("content exclusion policy", StringComparison.OrdinalIgnoreCase))
        {
            return rawOutput.Trim();
        }

        return "Agent response was not valid JSON";
    }

    private static string? ResolveAgentWorkingDirectory()
    {
        string agentFileName = $"{AgentName}.agent.md";
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string? startPath in new string?[]
            {
                Environment.CurrentDirectory,
                AppContext.BaseDirectory,
                Path.GetDirectoryName(typeof(CompatibilityAgent).Assembly.Location),
            })
        {
            if (string.IsNullOrWhiteSpace(startPath))
            {
                continue;
            }

            string? resolved = FindAgentAncestor(startPath, agentFileName);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                _ = roots.Add(resolved);
            }
        }

        return roots.FirstOrDefault();
    }

    private static string? FindAgentAncestor(string startPath, string agentFileName)
    {
        DirectoryInfo? directory = new(Path.GetFullPath(startPath));
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, ".github", "agents", agentFileName);
            if (File.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
