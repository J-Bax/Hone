namespace Hone.Core.Models;

/// <summary>
/// Scope of an improvement opportunity identified by the analyst.
/// </summary>
public enum OpportunityScope
{
    /// <summary>Uninitialized or unknown scope.</summary>
    Unknown = 0,

    /// <summary>A narrow, targeted change.</summary>
    Narrow = 1,

    /// <summary>A broader architectural change.</summary>
    Architecture = 2,
}
