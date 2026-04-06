namespace Hone.Core.Config;

/// <summary>
/// Configuration for Hone logging.
/// </summary>
public sealed record LoggingConfig(
    string Level = "info",
    int MaxFileSizeMB = 50);
