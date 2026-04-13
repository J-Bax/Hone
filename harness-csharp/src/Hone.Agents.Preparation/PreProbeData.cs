using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Hone.Agents.Preparation;

/// <summary>
/// Lightweight discovery data gathered before invoking the compatibility agent.
/// </summary>
[SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Mutable DTO populated by pre-prober")]
[SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "JSON DTO previously internal")]
[SuppressMessage("Design", "MA0016:Prefer using collection abstraction instead of implementation", Justification = "JSON DTO previously internal")]
public sealed class PreProbeData
{
    [JsonPropertyName("targetPath")]
    public string TargetPath { get; init; } = string.Empty;

    [JsonPropertyName("git")]
    public GitInfo Git { get; init; } = new();

    [JsonPropertyName("projectFiles")]
    public Dictionary<string, List<string>> ProjectFiles { get; init; } = [];

    [JsonPropertyName("topLevelDirs")]
    public List<string> TopLevelDirs { get; init; } = [];

    [JsonPropertyName("topLevelFiles")]
    public List<string> TopLevelFiles { get; init; } = [];

    [JsonPropertyName("existingHoneDir")]
    public bool ExistingHoneDir { get; init; }

    [JsonPropertyName("honeDirContents")]
    public List<string>? HoneDirContents { get; init; }

    [JsonPropertyName("detectedSourceCodePaths")]
    public List<string>? DetectedSourceCodePaths { get; init; }

    [JsonPropertyName("detectedSourceFileGlob")]
    public string? DetectedSourceFileGlob { get; init; }

    [JsonPropertyName("detectedStack")]
    public string? DetectedStack { get; init; }

    [JsonPropertyName("legacyHarness")]
    public LegacyHarnessInfo? LegacyHarness { get; init; }
}

/// <summary>
/// Metadata about a detected PowerShell-based legacy harness.
/// </summary>
public sealed record LegacyHarnessInfo
{
    [JsonPropertyName("detected")]
    public bool Detected { get; init; }

    [JsonPropertyName("configPsd1Path")]
    public string? ConfigPsd1Path { get; init; }

    [JsonPropertyName("hookScripts")]
    public IReadOnlyList<string>? HookScripts { get; init; }
}

/// <summary>
/// Git repository metadata.
/// </summary>
public sealed class GitInfo
{
    [JsonPropertyName("isGitRepo")]
    public bool IsGitRepo { get; init; }

    [JsonPropertyName("remoteUrl")]
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "JSON DTO")]
    public string? RemoteUrl { get; init; }

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; init; }
}
