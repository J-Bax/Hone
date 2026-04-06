using Hone.Core.Config;

namespace Hone.Core.Contracts;

/// <summary>
/// Context passed to lifecycle hook execution.
/// </summary>
public sealed record HookContext(
    string TargetPath,
    HoneConfig Config,
    Uri? BaseUrl,
    int Experiment);
