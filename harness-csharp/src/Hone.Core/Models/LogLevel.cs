namespace Hone.Core.Models;

/// <summary>
/// Severity level for Hone log messages.
/// </summary>
public enum LogLevel
{
    /// <summary>Detailed diagnostic information.</summary>
    Verbose,

    /// <summary>General informational messages.</summary>
    Info,

    /// <summary>Potential issues that are not errors.</summary>
    Warning,

    /// <summary>Errors that require attention.</summary>
    Error,
}
