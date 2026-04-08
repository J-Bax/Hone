namespace Hone.Lifecycle.Hooks;

/// <summary>
/// Result of resolving a hook definition from target configuration.
/// </summary>
public sealed record ResolvedHook(
    HookType Type,
    string? BuiltInName,
    string? Command,
    Uri? Url,
    string? HttpMethod)
{
    /// <summary>
    /// Creates a resolved hook for a built-in C# implementation.
    /// </summary>
    /// <param name="builtInName">
    /// The registry key (e.g. <c>dotnet-stop</c>) used to look up
    /// the <see cref="IBuiltInHookRegistry"/> implementation.
    /// </param>
    public static ResolvedHook BuiltIn(string builtInName) =>
        new(Type: HookType.BuiltIn, BuiltInName: builtInName, Command: null, Url: null, HttpMethod: null);

    /// <summary>
    /// Creates a resolved hook for a shell command.
    /// </summary>
    public static ResolvedHook ForCommand(string command) =>
        new(Type: HookType.Command, BuiltInName: null, Command: command, Url: null, HttpMethod: null);

    /// <summary>
    /// Creates a resolved hook for an HTTP request.
    /// </summary>
    public static ResolvedHook ForHttp(Uri url, string? method = null) =>
        new(Type: HookType.Http, BuiltInName: null, Command: null, Url: url, HttpMethod: method ?? "GET");

    /// <summary>
    /// Creates a resolved hook that skips execution.
    /// </summary>
    public static ResolvedHook Skipped() =>
        new(Type: HookType.Skip, BuiltInName: null, Command: null, Url: null, HttpMethod: null);
}
