using System.Text.Json.Serialization;

namespace Hone.Agents.Preparation;

/// <summary>
/// Lightweight discovery data gathered before invoking the compatibility agent.
/// </summary>
internal sealed class PreProbeData
{
    [JsonPropertyName("targetPath")]
    public string TargetPath { get; set; } = string.Empty;

    [JsonPropertyName("git")]
    public GitInfo Git { get; set; } = new();

    [JsonPropertyName("projectFiles")]
    public Dictionary<string, List<string>> ProjectFiles { get; set; } = [];

    [JsonPropertyName("topLevelDirs")]
    public List<string> TopLevelDirs { get; set; } = [];

    [JsonPropertyName("topLevelFiles")]
    public List<string> TopLevelFiles { get; set; } = [];

    [JsonPropertyName("existingHoneDir")]
    public bool ExistingHoneDir { get; set; }

    [JsonPropertyName("honeDirContents")]
    public List<string>? HoneDirContents { get; set; }
}

/// <summary>
/// Git repository metadata.
/// </summary>
internal sealed class GitInfo
{
    [JsonPropertyName("isGitRepo")]
    public bool IsGitRepo { get; set; }

    [JsonPropertyName("remoteUrl")]
    public string? RemoteUrl { get; set; }

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; set; }
}
