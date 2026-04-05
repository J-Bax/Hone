namespace Hone.Lifecycle.Hooks;

/// <summary>
/// Discriminator for hook resolution.
/// BuiltIn replaces the PowerShell Script and Shared types with native C# implementations.
/// </summary>
public enum HookType
{
    /// <summary>Native C# hook implementation (replaces PS Script + Shared).</summary>
    BuiltIn,

    /// <summary>Runs a shell command string.</summary>
    Command,

    /// <summary>Makes an HTTP request to the configured URL.</summary>
    Http,

    /// <summary>No-op; hook is intentionally skipped.</summary>
    Skip,
}
