using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Hone.Agents.Core;

namespace Hone.Agents.Preparation;

/// <summary>
/// Translated configuration and hook mappings produced by the hone-migrator agent.
/// </summary>
[SuppressMessage("Usage", "MA0016:Prefer using collection abstraction instead of implementation", Justification = "JSON DTO requires concrete Dictionary for polymorphic deserialization")]
public sealed record MigrationPlan
{
    [JsonPropertyName("config")]
    public Dictionary<string, object>? Config { get; init; }

    [JsonPropertyName("hookMappings")]
    public IReadOnlyList<HookMapping>? HookMappings { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string>? Warnings { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

/// <summary>
/// Describes how a single legacy PowerShell hook maps to a C# harness hook.
/// </summary>
public sealed record HookMapping
{
    [JsonPropertyName("originalScript")]
    public string? OriginalScript { get; init; }

    [JsonPropertyName("mappedTo")]
    public string? MappedTo { get; init; }

    [JsonPropertyName("confidence")]
    public string? Confidence { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

/// <summary>
/// Result returned by <see cref="MigratorAgent.MigrateAsync"/>.
/// </summary>
public sealed record MigrationResult(
    bool Success,
    string Message,
    MigrationPlan? Plan);

/// <summary>
/// Invokes the hone-migrator agent to translate a PowerShell legacy harness
/// configuration into the Hone C# harness format.
/// </summary>
public sealed class MigratorAgent
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string AgentName = "hone-migrator";
    private const string DefaultModel = ModelDefaults.Migrator;
    private const int MaxRetries = 1;
    private const string RetryPromptSuffix =
        "Your previous response was not valid JSON. Please respond with ONLY a JSON object matching the output schema.";

    private readonly AgentInvoker _invoker;

    public MigratorAgent(AgentInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        _invoker = invoker;
    }

    /// <summary>
    /// Translates a legacy PowerShell harness into a C# harness migration plan.
    /// </summary>
    /// <param name="preProbe">Pre-probe data containing legacy harness file paths.</param>
    /// <param name="report">The compatibility assessment report.</param>
    /// <param name="model">Optional model override.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="MigrationResult"/> with the translation plan.</returns>
    public async Task<MigrationResult> MigrateAsync(
        PreProbeData preProbe,
        CompatibilityReport report,
        string? model = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preProbe);
        ArgumentNullException.ThrowIfNull(report);

        string prompt = await BuildPromptAsync(preProbe, report, ct).ConfigureAwait(false);

        var options = new AgentInvocationOptions(
            AgentName: AgentName,
            Prompt: prompt,
            DefaultModel: model ?? DefaultModel,
            MaxRetries: MaxRetries,
            RetryPromptSuffix: RetryPromptSuffix,
            WorkingDirectory: preProbe.TargetPath);

        AgentResult<MigrationPlan> agentResult =
            await _invoker.InvokeAgentAsync<MigrationPlan>(options, ct).ConfigureAwait(false);

        if (agentResult.TimedOut)
        {
            return new MigrationResult(
                Success: false,
                Message: "Agent timed out",
                Plan: null);
        }

        if (!agentResult.Success || agentResult.ParsedResult is null)
        {
            return new MigrationResult(
                Success: false,
                Message: "Agent response was not valid JSON",
                Plan: null);
        }

        MigrationPlan plan = agentResult.ParsedResult;

        return new MigrationResult(
            Success: true,
            Message: FormatSummaryMessage(plan),
            Plan: plan);
    }

    private static async Task<string> BuildPromptAsync(
        PreProbeData preProbe,
        CompatibilityReport report,
        CancellationToken ct)
    {
        string reportJson = JsonSerializer.Serialize(report, SerializeOptions);

        var sb = new StringBuilder();
        sb.AppendLine("Translate the following PowerShell legacy harness into C# harness format.");
        sb.AppendLine();

        // Read config.psd1 if available
        if (preProbe.LegacyHarness?.ConfigPsd1Path is not null
            && File.Exists(preProbe.LegacyHarness.ConfigPsd1Path))
        {
            string configContent = await File.ReadAllTextAsync(
                preProbe.LegacyHarness.ConfigPsd1Path, ct).ConfigureAwait(false);

            sb.AppendLine("## Legacy config.psd1");
            sb.AppendLine();
            sb.AppendLine("```powershell");
            sb.AppendLine(configContent);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        // Read hook scripts if available
        if (preProbe.LegacyHarness?.HookScripts is { Count: > 0 })
        {
            sb.AppendLine("## Legacy Hook Scripts");
            sb.AppendLine();

            foreach (string scriptPath in preProbe.LegacyHarness.HookScripts)
            {
                if (!File.Exists(scriptPath))
                {
                    continue;
                }

                string scriptContent = await File.ReadAllTextAsync(scriptPath, ct)
                    .ConfigureAwait(false);
                string fileName = Path.GetFileName(scriptPath);

                sb.AppendLine(CultureInfo.InvariantCulture, $"### {fileName}");
                sb.AppendLine();
                sb.AppendLine("```powershell");
                sb.AppendLine(scriptContent);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Compatibility Assessment Report");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(reportJson);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Instructions");
        sb.AppendLine();
        sb.AppendLine("1. Translate all config.psd1 settings to the C# harness config format.");
        sb.AppendLine("2. Map each hook script to a BuiltIn hook where behavior matches.");
        sb.AppendLine("3. Generate Command hooks for scripts with custom logic.");
        sb.AppendLine("4. Flag any settings that have no C# harness equivalent.");
        sb.AppendLine("5. Produce ONLY the JSON output matching the output schema. No other text.");

        return sb.ToString();
    }

    private static string FormatSummaryMessage(MigrationPlan plan)
    {
        int mappingCount = plan.HookMappings?.Count ?? 0;
        int warningCount = plan.Warnings?.Count ?? 0;

        return warningCount > 0
            ? $"Migration plan generated with {mappingCount} hook mapping(s) and {warningCount} warning(s)"
            : $"Migration plan generated with {mappingCount} hook mapping(s)";
    }
}
