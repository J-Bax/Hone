using System.Collections.Frozen;
using System.Text.Json;

using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Utilities;

namespace Hone.Agents.Core;

/// <summary>
/// Generic AI agent invoker that handles model resolution, JSON extraction,
/// retry logic, and timeout propagation.
/// </summary>
public sealed class AgentInvoker
{
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IAgentRunner _runner;
    private readonly AgentConfig _agentConfig;
    private readonly FrozenDictionary<string, string?> _modelOverrides;

    public AgentInvoker(IAgentRunner runner, AgentConfig agentConfig)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(agentConfig);

        _runner = runner;
        _agentConfig = agentConfig;

        // Build a lookup of ModelConfigKey → AgentConfig property values.
        _modelOverrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["AnalysisModel"] = agentConfig.AnalysisModel,
            ["ClassificationModel"] = agentConfig.ClassificationModel,
            ["ImplementerModel"] = agentConfig.ImplementerModel,
            ["CriticModel"] = agentConfig.CriticModel,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Invokes an AI agent, resolves the model and timeout, extracts JSON from
    /// the response, and returns a strongly-typed result with retry support.
    /// </summary>
    public async Task<AgentResult<T>> InvokeAgentAsync<T>(
        AgentInvocationOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        string model = ResolveModel(options);
        var timeout = TimeSpan.FromSeconds(_agentConfig.AgentTimeoutSec);

        AgentRunResult? lastRunResult = null;

        for (int attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            string effectivePrompt = attempt > 0 && options.RetryPromptSuffix is not null
                ? $"{options.Prompt}\n\n{options.RetryPromptSuffix}"
                : options.Prompt;

            var invocation = new AgentInvocation(
                AgentName: options.AgentName,
                Prompt: effectivePrompt,
                Model: model,
                Timeout: timeout,
                WorkingDirectory: options.WorkingDirectory);

            try
            {
                lastRunResult = await _runner.InvokeAsync(invocation, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate resilience boundary
            catch (Exception ex)
            {
                return new AgentResult<T>(
                    Success: false,
                    ParsedResult: default,
                    RawOutput: ex.Message,
                    ResponseText: string.Empty,
                    TimedOut: false,
                    ExitCode: -1);
            }
#pragma warning restore CA1031

            if (lastRunResult.TimedOut)
            {
                return new AgentResult<T>(
                    Success: false,
                    ParsedResult: default,
                    RawOutput: lastRunResult.Output,
                    ResponseText: string.Empty,
                    TimedOut: true,
                    ExitCode: lastRunResult.ExitCode);
            }

            // Non-zero exit code — return failure immediately without consuming a retry
            if (lastRunResult.ExitCode != 0)
            {
                return new AgentResult<T>(
                    Success: false,
                    ParsedResult: default,
                    RawOutput: lastRunResult.Output,
                    ResponseText: string.Empty,
                    TimedOut: false,
                    ExitCode: lastRunResult.ExitCode);
            }

            string extracted = JsonUtils.ExtractJsonBlock(lastRunResult.Output);
            string sanitized = JsonUtils.SanitizeNaN(extracted);

            try
            {
                T? parsed = JsonSerializer.Deserialize<T>(sanitized, DeserializeOptions);

                return new AgentResult<T>(
                    Success: true,
                    ParsedResult: parsed,
                    RawOutput: lastRunResult.Output,
                    ResponseText: sanitized,
                    TimedOut: false,
                    ExitCode: lastRunResult.ExitCode);
            }
            catch (JsonException)
            {
                // Parse failed — retry if attempts remain
            }
        }

        // All retries exhausted with no successful JSON parse.
        return new AgentResult<T>(
            Success: false,
            ParsedResult: default,
            RawOutput: lastRunResult?.Output ?? string.Empty,
            ResponseText: string.Empty,
            TimedOut: false,
            ExitCode: lastRunResult?.ExitCode ?? -1);
    }

    private string ResolveModel(AgentInvocationOptions options)
    {
        // 1. Per-agent model override from AgentConfig (via ModelConfigKey)
        if (options.ModelConfigKey is not null
            && _modelOverrides.TryGetValue(options.ModelConfigKey, out string? configModel)
            && configModel is not null)
        {
            return configModel;
        }

        // 2. Caller-supplied default model
        if (options.DefaultModel is not null)
        {
            return options.DefaultModel;
        }

        // 3. Global default from AgentConfig
        return _agentConfig.DefaultModel;
    }
}
