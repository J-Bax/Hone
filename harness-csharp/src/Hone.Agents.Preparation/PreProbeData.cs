using System.Text.Json.Serialization;

namespace Hone.Agents.Preparation;

/// <summary>
/// Lightweight discovery data gathered before invoking the compatibility agent.
/// </summary>
internal sealed class PreProbeData
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
}

/// <summary>
/// Git repository metadata.
/// </summary>
internal sealed class GitInfo
{
    [JsonPropertyName("isGitRepo")]
    public bool IsGitRepo { get; init; }

    [JsonPropertyName("remoteUrl")]
    public string? RemoteUrl { get; init; }

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; init; }
}
