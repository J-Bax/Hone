namespace Hone.Core.Config;

/// <summary>
/// Configuration for the iterative implementer (fixer).
/// </summary>
public sealed record ImplementerConfig(
    int MaxAttempts = 3,
    double MaxDiffGrowthFactor = 3.0,
    bool TestFileGuard = true);
