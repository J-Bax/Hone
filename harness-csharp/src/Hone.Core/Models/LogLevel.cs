namespace Hone.Core.Models;

/// <summary>
/// Severity level for Hone log messages.
/// </summary>
public enum LogLevel
{
    /// <summary>Uninitialized or unknown log level.</summary>
    Unknown = 0,

    /// <summary>Detailed diagnostic information.</summary>
    Verbose = 1,

    /// <summary>General informational messages.</summary>
    Info = 2,

    /// <summary>Potential issues that are not errors.</summary>
    Warning = 3,

    /// <summary>Errors that require attention.</summary>
    Error = 4,
}
