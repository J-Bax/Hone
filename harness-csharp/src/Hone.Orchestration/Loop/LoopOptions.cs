using Hone.Core.Config;

namespace Hone.Orchestration.Loop;

/// <summary>
/// Configuration for a single <see cref="HoneLoopRunner.RunAsync"/> invocation.
/// </summary>
internal sealed record LoopOptions(
    string TargetDir,
    HoneConfig Config,
    string? TargetName = null,
    string DefaultBranch = "main",
    string ResultsPath = "hone-results",
    bool DryRun = false,
    int? MaxExperimentsOverride = null);
