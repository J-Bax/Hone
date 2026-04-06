namespace Hone.Core.Contracts;

/// <summary>
/// Options for running a load test.
/// </summary>
public sealed record LoadTestOptions(
    string ScenarioPath,
    Uri BaseUrl,
    string OutputDir,
    int Experiment,
    int Run,
    TimeSpan? Timeout,
    IReadOnlyDictionary<string, string>? EnvironmentVars = null);
