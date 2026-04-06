namespace Hone.Core.Config;

/// <summary>
/// Configuration for k6 scale testing.
/// </summary>
public sealed record ScaleTestConfig(
    string ScenarioPath = "sample-api/scale-tests/scenarios/baseline.js",
    string? ScenarioRegistryPath = "sample-api/scale-tests/thresholds.json",
    IReadOnlyList<string>? ExtraArgs = null,
    bool WarmupEnabled = true,
    string? WarmupScenarioPath = "sample-api/scale-tests/scenarios/warmup.js",
    int MeasuredRuns = 5,
    int CooldownSeconds = 3)
{
    /// <summary>
    /// Gets additional k6 CLI arguments.
    /// </summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = ExtraArgs ?? [];
}
