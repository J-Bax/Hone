using System.Text.Json.Serialization;

namespace Hone.Agents.Preparation;

/// <summary>
/// Structured compatibility assessment report returned by the hone-compatibility agent.
/// </summary>
public sealed class CompatibilityReport
{
    /// <summary>Overall compatibility verdict, score, blockers, warnings, and ready items.</summary>
    [JsonPropertyName("compatibility")]
    public CompatibilitySection? Compatibility { get; set; }

    /// <summary>Detected target stack and framework information.</summary>
    [JsonPropertyName("target")]
    public TargetSection? Target { get; set; }

    /// <summary>Suggested onboarding plan for the target.</summary>
    [JsonPropertyName("onboardingPlan")]
    public OnboardingPlanSection? OnboardingPlan { get; set; }
}

/// <summary>
/// Compatibility verdict with findings.
/// </summary>
public sealed class CompatibilitySection
{
    /// <summary>Overall verdict such as "compatible", "incompatible", or "partial".</summary>
    [JsonPropertyName("overall")]
    public string? Overall { get; set; }

    /// <summary>Numeric score from 0 to 100.</summary>
    [JsonPropertyName("score")]
    public int? Score { get; set; }

    /// <summary>Blocking issues that must be resolved before onboarding.</summary>
    [JsonPropertyName("blockers")]
    public IReadOnlyList<CompatibilityFinding>? Blockers { get; set; }

    /// <summary>Non-blocking issues that may affect onboarding quality.</summary>
    [JsonPropertyName("warnings")]
    public IReadOnlyList<CompatibilityFinding>? Warnings { get; set; }

    /// <summary>Areas that are already compatible.</summary>
    [JsonPropertyName("ready")]
    public IReadOnlyList<ReadyItem>? Ready { get; set; }
}

/// <summary>
/// A blocker or warning finding.
/// </summary>
public sealed class CompatibilityFinding
{
    /// <summary>Area of concern (e.g. "build", "tests", "endpoints").</summary>
    [JsonPropertyName("area")]
    public string? Area { get; set; }

    /// <summary>Description of the issue.</summary>
    [JsonPropertyName("issue")]
    public string? Issue { get; set; }

    /// <summary>Suggested remediation steps.</summary>
    [JsonPropertyName("remediation")]
    public string? Remediation { get; set; }
}

/// <summary>
/// An area that is ready / compatible.
/// </summary>
public sealed class ReadyItem
{
    /// <summary>Area that is ready (e.g. "build", "tests").</summary>
    [JsonPropertyName("area")]
    public string? Area { get; set; }

    /// <summary>Details about the ready area.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}

/// <summary>
/// Detected target stack information.
/// </summary>
public sealed class TargetSection
{
    /// <summary>Detected technology stack (e.g. ".NET", "Node.js").</summary>
    [JsonPropertyName("detectedStack")]
    public string? DetectedStack { get; set; }

    /// <summary>Detected framework (e.g. "ASP.NET Core 8.0").</summary>
    [JsonPropertyName("detectedFramework")]
    public string? DetectedFramework { get; set; }
}

/// <summary>
/// Onboarding plan section of the compatibility report.
/// </summary>
public sealed class OnboardingPlanSection
{
    /// <summary>High-level summary of the onboarding plan.</summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; set; }
}
