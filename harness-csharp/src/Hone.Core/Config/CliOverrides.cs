namespace Hone.Core.Config;

/// <summary>
/// Optional CLI flag overrides. All properties are nullable because
/// CLI flags are optional; <see langword="null"/> means "use config value".
/// </summary>
public sealed record CliOverrides(
    int? MaxExperiments = null,
    string? Model = null,
    bool? StackedDiffs = null,
    bool? WaitForMerge = null,
    bool? SkipClassification = null,
    bool? DiagnosticsEnabled = null);
