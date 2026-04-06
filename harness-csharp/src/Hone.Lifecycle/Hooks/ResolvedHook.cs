namespace Hone.Lifecycle.Hooks;

/// <summary>
/// Result of resolving a hook definition from target configuration.
/// </summary>
public sealed record ResolvedHook(
    HookType Type,
    string? Command,
    Uri? Url,
    string? HttpMethod)
{
    /// <summary>
    /// Creates a resolved hook for a built-in C# implementation.
    /// </summary>
    public static ResolvedHook BuiltIn() =>
        new(Type: HookType.BuiltIn, Command: null, Url: null, HttpMethod: null);

    /// <summary>
    /// Creates a resolved hook for a shell command.
    /// </summary>
    public static ResolvedHook ForCommand(string command) =>
        new(Type: HookType.Command, Command: command, Url: null, HttpMethod: null);

    /// <summary>
    /// Creates a resolved hook for an HTTP request.
    /// </summary>
    public static ResolvedHook ForHttp(Uri url, string? method = null) =>
        new(Type: HookType.Http, Command: null, Url: url, HttpMethod: method ?? "GET");

    /// <summary>
    /// Creates a resolved hook that skips execution.
    /// </summary>
    public static ResolvedHook Skipped() =>
        new(Type: HookType.Skip, Command: null, Url: null, HttpMethod: null);
}
