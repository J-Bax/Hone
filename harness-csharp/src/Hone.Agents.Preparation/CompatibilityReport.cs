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
    [JsonPropertyName("detectedStack")]
    public string? DetectedStack { get; init; }

    [JsonPropertyName("detectedFramework")]
    public string? DetectedFramework { get; init; }
}

public sealed record OnboardingPlanSection
{
    [JsonPropertyName("summary")]
    public string? Summary { get; init; }
}
