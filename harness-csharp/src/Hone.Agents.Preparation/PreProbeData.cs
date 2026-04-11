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

    [JsonPropertyName("legacyHarness")]
    public LegacyHarnessInfo? LegacyHarness { get; init; }
}

/// <summary>
/// Metadata about a detected PowerShell-based legacy harness.
/// </summary>
internal sealed record LegacyHarnessInfo
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
internal sealed class GitInfo
{
    [JsonPropertyName("isGitRepo")]
    public bool IsGitRepo { get; init; }

    [JsonPropertyName("remoteUrl")]
    public string? RemoteUrl { get; init; }

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; init; }
}
