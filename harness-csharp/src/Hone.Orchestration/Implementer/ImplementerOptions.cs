using Hone.Core.Config;

namespace Hone.Orchestration.Implementer;

/// <summary>
/// All inputs needed by <see cref="IterativeImplementerRunner"/> for a single experiment.
/// </summary>
internal sealed record ImplementerOptions(
    string FilePath,
    string Explanation,
    string? RootCauseDocument,
    int Experiment,
    string BaseBranch,
    string TargetDir,
    string? TargetName,
    ImplementerConfig Config,
    CriticConfig CriticConfig,
    IReadOnlyList<string>? TestProjectPaths,
    string BranchPrefix,
    string ResultsPath,
    string? ClassificationScope = null);
