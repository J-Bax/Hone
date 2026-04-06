namespace Hone.Lifecycle.Hooks;

/// <summary>
/// Discriminator for hook resolution.
/// BuiltIn uses native C# implementations.
/// </summary>
public enum HookType
{
    /// <summary>Uninitialized or unknown hook type.</summary>
    Unknown = 0,

    /// <summary>Native C# hook implementation.</summary>
    BuiltIn = 1,

    /// <summary>Runs a shell command string.</summary>
    Command = 2,

    /// <summary>Makes an HTTP request to the configured URL.</summary>
    Http = 3,

    /// <summary>No-op; hook is intentionally skipped.</summary>
    Skip = 4,
}
