using Hone.Core.Config;
using Hone.Core.Models;

namespace Hone.Orchestration.Loop;

internal sealed record ExperimentRunContext(
    int Experiment,
    DateTimeOffset StartedAt,
    string BranchName,
    string BaseBranch,
    LoopState State,
    HoneConfig Config,
    LoopOptions Options,
    string TargetDir,
    string ResultsPath,
    string TargetName,
    MachineInfo MachineInfo,
    List<ExperimentMetadata> Experiments,
    string MetadataPath,
    string ExpectedStableHeadSha,
    string CleanupManifestPath);
