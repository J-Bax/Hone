namespace Hone.Core.Config;

/// <summary>
/// Root configuration for the Hone optimization harness.
/// Aggregates all configuration sections with defaults matching <c>config.psd1</c>.
/// </summary>
public sealed record HoneConfig(
    ApiConfig? Api = null,
    TolerancesConfig? Tolerances = null,
    ScaleTestConfig? ScaleTest = null,
    LoopConfig? Loop = null,
    AgentConfig? Agents = null,
    DiagnosticsConfig? Diagnostics = null,
    LoggingConfig? Logging = null,
    ImplementerConfig? Implementer = null,
    DotnetCountersConfig? DotnetCounters = null)
{
    /// <summary>Gets the API configuration.</summary>
    public ApiConfig Api { get; init; } = Api ?? new ApiConfig();

    /// <summary>Gets the performance tolerance settings.</summary>
    public TolerancesConfig Tolerances { get; init; } = Tolerances ?? new TolerancesConfig();

    /// <summary>Gets the scale test configuration.</summary>
    public ScaleTestConfig ScaleTest { get; init; } = ScaleTest ?? new ScaleTestConfig();

    /// <summary>Gets the optimization loop configuration.</summary>
    public LoopConfig Loop { get; init; } = Loop ?? new LoopConfig();

    /// <summary>Gets the AI agent configuration.</summary>
    public AgentConfig Agents { get; init; } = Agents ?? new AgentConfig();

    /// <summary>Gets the diagnostic profiling configuration.</summary>
    public DiagnosticsConfig Diagnostics { get; init; } = Diagnostics ?? new DiagnosticsConfig();

    /// <summary>Gets the logging configuration.</summary>
    public LoggingConfig Logging { get; init; } = Logging ?? new LoggingConfig();

    /// <summary>Gets the iterative implementer configuration.</summary>
    public ImplementerConfig Implementer { get; init; } = Implementer ?? new ImplementerConfig();

    /// <summary>Gets the .NET counters configuration.</summary>
    public DotnetCountersConfig DotnetCounters { get; init; } = DotnetCounters ?? new DotnetCountersConfig();
}
