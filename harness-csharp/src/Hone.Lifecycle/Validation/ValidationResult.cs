namespace Hone.Lifecycle.Validation;

/// <summary>
/// Result of configuration validation.
/// </summary>
public sealed record ValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);
