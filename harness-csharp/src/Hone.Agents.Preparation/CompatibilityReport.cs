using System.Text.Json.Serialization;

namespace Hone.Agents.Preparation;

/// <summary>
/// Structured compatibility assessment report returned by the hone-compatibility agent.
/// </summary>
public sealed record CompatibilityReport
{
    [JsonPropertyName("compatibility")]
    public CompatibilitySection? Compatibility { get; init; }

    [JsonPropertyName("target")]
    public TargetSection? Target { get; init; }

    [JsonPropertyName("onboardingPlan")]
    public OnboardingPlanSection? OnboardingPlan { get; init; }

    [JsonPropertyName("probeResults")]
    public ProbeResultsSection? ProbeResults { get; init; }

    [JsonPropertyName("detectedConfig")]
    public DetectedConfigSection? DetectedConfig { get; init; }

    [JsonPropertyName("implementationPlan")]
    public ImplementationPlanSection? ImplementationPlan { get; init; }
}

public sealed record CompatibilitySection
{
    [JsonPropertyName("overall")]
    public string? Overall { get; init; }

    [JsonPropertyName("score")]
    public int? Score { get; init; }

    [JsonPropertyName("blockers")]
    public IReadOnlyList<CompatibilityFinding>? Blockers { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<CompatibilityFinding>? Warnings { get; init; }

    [JsonPropertyName("ready")]
    public IReadOnlyList<ReadyItem>? Ready { get; init; }
}

public sealed record CompatibilityFinding
{
    [JsonPropertyName("area")]
    public string? Area { get; init; }

    [JsonPropertyName("issue")]
    public string? Issue { get; init; }

    [JsonPropertyName("remediation")]
    public string? Remediation { get; init; }
}

public sealed record ReadyItem
{
    [JsonPropertyName("area")]
    public string? Area { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

public sealed record TargetSection
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("detectedStack")]
    public string? DetectedStack { get; init; }

    [JsonPropertyName("detectedFramework")]
    public string? DetectedFramework { get; init; }

    [JsonPropertyName("detectedRuntime")]
    public string? DetectedRuntime { get; init; }
}

public sealed record OnboardingPlanSection
{
    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("phases")]
    public IReadOnlyList<OnboardingPhase>? Phases { get; init; }
}

public sealed record OnboardingPhase
{
    [JsonPropertyName("phase")]
    public string? Phase { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("steps")]
    public IReadOnlyList<string>? Steps { get; init; }
}

// -- Probe Results -------------------------------------------------------

public sealed record ProbeResultsSection
{
    [JsonPropertyName("git")]
    public GitProbeResult? Git { get; init; }

    [JsonPropertyName("build")]
    public BuildProbeResult? Build { get; init; }

    [JsonPropertyName("tests")]
    public TestProbeResult? Tests { get; init; }

    [JsonPropertyName("api")]
    public ApiProbeResult? Api { get; init; }

    [JsonPropertyName("database")]
    public DatabaseProbeResult? Database { get; init; }

    [JsonPropertyName("externalDeps")]
    public ExternalDepsProbeResult? ExternalDeps { get; init; }

    [JsonPropertyName("k6")]
    public K6ProbeResult? K6 { get; init; }

    [JsonPropertyName("honeDir")]
    public HoneDirProbeResult? HoneDir { get; init; }
}

public sealed record GitProbeResult
{
    [JsonPropertyName("isGitRepo")]
    public bool IsGitRepo { get; init; }

    [JsonPropertyName("remoteUrl")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "JSON DTO")]
    public string? RemoteUrl { get; init; }

    [JsonPropertyName("isGitHub")]
    public bool IsGitHub { get; init; }

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; init; }
}

public sealed record BuildProbeResult
{
    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("durationSeconds")]
    public double? DurationSeconds { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

public sealed record TestProbeResult
{
    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("totalTests")]
    public int? TotalTests { get; init; }

    [JsonPropertyName("passedTests")]
    public int? PassedTests { get; init; }

    [JsonPropertyName("failedTests")]
    public int? FailedTests { get; init; }

    [JsonPropertyName("framework")]
    public string? Framework { get; init; }

    [JsonPropertyName("testStyle")]
    public string? TestStyle { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

public sealed record ApiProbeResult
{
    [JsonPropertyName("framework")]
    public string? Framework { get; init; }

    [JsonPropertyName("healthEndpoint")]
    public string? HealthEndpoint { get; init; }

    [JsonPropertyName("gcEndpoint")]
    public string? GcEndpoint { get; init; }

    [JsonPropertyName("supportsEphemeralPort")]
    public bool? SupportsEphemeralPort { get; init; }

    [JsonPropertyName("endpoints")]
    public IReadOnlyList<ApiEndpoint>? Endpoints { get; init; }

    [JsonPropertyName("authRequired")]
    public bool? AuthRequired { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

public sealed record ApiEndpoint
{
    [JsonPropertyName("method")]
    public string? Method { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }
}

public sealed record DatabaseProbeResult
{
    [JsonPropertyName("detected")]
    public bool Detected { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("orm")]
    public string? Orm { get; init; }

    [JsonPropertyName("connectionStringSource")]
    public string? ConnectionStringSource { get; init; }

    [JsonPropertyName("hasMigrations")]
    public bool? HasMigrations { get; init; }

    [JsonPropertyName("hasSeedData")]
    public bool? HasSeedData { get; init; }

    [JsonPropertyName("resetStrategy")]
    public string? ResetStrategy { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

public sealed record ExternalDepsProbeResult
{
    [JsonPropertyName("httpClients")]
    public IReadOnlyList<string>? HttpClients { get; init; }

    [JsonPropertyName("messageQueues")]
    public IReadOnlyList<string>? MessageQueues { get; init; }

    [JsonPropertyName("caches")]
    public IReadOnlyList<string>? Caches { get; init; }

    [JsonPropertyName("docker")]
    public bool? Docker { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

public sealed record K6ProbeResult
{
    [JsonPropertyName("existingScenarios")]
    public bool? ExistingScenarios { get; init; }

    [JsonPropertyName("scenarioFiles")]
    public IReadOnlyList<string>? ScenarioFiles { get; init; }

    [JsonPropertyName("estimatedEndpoints")]
    public int? EstimatedEndpoints { get; init; }

    [JsonPropertyName("authComplexity")]
    public string? AuthComplexity { get; init; }

    [JsonPropertyName("estimatedEffort")]
    public string? EstimatedEffort { get; init; }
}

public sealed record HoneDirProbeResult
{
    [JsonPropertyName("exists")]
    public bool Exists { get; init; }

    [JsonPropertyName("valid")]
    public bool? Valid { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

// -- Detected Config -----------------------------------------------------

public sealed record DetectedConfigSection
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("baseBranch")]
    public string? BaseBranch { get; init; }

    [JsonPropertyName("solutionPath")]
    public string? SolutionPath { get; init; }

    [JsonPropertyName("projectPath")]
    public string? ProjectPath { get; init; }

    [JsonPropertyName("testProjectPath")]
    public string? TestProjectPath { get; init; }

    [JsonPropertyName("sourceCodePaths")]
    public IReadOnlyList<string>? SourceCodePaths { get; init; }

    [JsonPropertyName("sourceFileGlob")]
    public string? SourceFileGlob { get; init; }

    [JsonPropertyName("healthEndpoint")]
    public string? HealthEndpoint { get; init; }

    [JsonPropertyName("gcEndpoint")]
    public string? GcEndpoint { get; init; }

    [JsonPropertyName("baseUrl")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "JSON DTO")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("startupTimeout")]
    public int? StartupTimeout { get; init; }

    [JsonPropertyName("databaseType")]
    public string? DatabaseType { get; init; }
}

// -- Implementation Plan -------------------------------------------------

public sealed record ImplementationPlanSection
{
    [JsonPropertyName("hookRecommendations")]
    public IReadOnlyDictionary<string, HookRecommendation>? HookRecommendations { get; init; }

    [JsonPropertyName("requiredCodeChanges")]
    public IReadOnlyList<RequiredCodeChange>? RequiredCodeChanges { get; init; }

    [JsonPropertyName("k6ScenarioGuidance")]
    public K6ScenarioGuidance? K6ScenarioGuidance { get; init; }

    [JsonPropertyName("configTemplate")]
    public string? ConfigTemplate { get; init; }
}

public sealed record HookRecommendation
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("method")]
    public string? Method { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record RequiredCodeChange
{
    [JsonPropertyName("file")]
    public string? File { get; init; }

    [JsonPropertyName("change")]
    public string? Change { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record K6ScenarioGuidance
{
    [JsonPropertyName("primaryEndpoints")]
    public IReadOnlyList<string>? PrimaryEndpoints { get; init; }

    [JsonPropertyName("suggestedWeights")]
    public IReadOnlyDictionary<string, int>? SuggestedWeights { get; init; }

    [JsonPropertyName("authSetup")]
    public string? AuthSetup { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}