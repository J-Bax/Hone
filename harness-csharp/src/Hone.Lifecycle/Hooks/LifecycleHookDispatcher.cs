using System.Diagnostics;
using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Lifecycle.Hooks;

/// <summary>
/// Dispatches resolved hooks to their execution strategy.
/// Replaces <c>Invoke-LifecycleHook</c> + <c>hooks/Invoke-Hook.ps1</c>.
/// </summary>
public sealed class LifecycleHookDispatcher(
    IBuiltInHookRegistry hookRegistry,
    IProcessRunner processRunner,
    HttpClient httpClient)
{
    public async Task<HookResult> DispatchAsync(
        string hookName,
        ResolvedHook hook,
        HookContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hook);
        ArgumentNullException.ThrowIfNull(context);

        var stopwatch = Stopwatch.StartNew();

        return hook.Type switch
        {
            HookType.BuiltIn => await DispatchBuiltInAsync(hookName, context, ct).ConfigureAwait(false),
            HookType.Command => await DispatchCommandAsync(hook, stopwatch, ct).ConfigureAwait(false),
            HookType.Http => await DispatchHttpAsync(hook, context, stopwatch, ct).ConfigureAwait(false),
            HookType.Skip => new HookResult(
                Success: true,
                Message: "Skipped",
                Duration: TimeSpan.Zero,
                Artifacts: [],
                BaseUrl: null),
            _ => new HookResult(
                Success: false,
                Message: $"Unknown hook type: {hook.Type}",
                Duration: stopwatch.Elapsed,
                Artifacts: [],
                BaseUrl: null),
        };
    }

    private async Task<HookResult> DispatchBuiltInAsync(
        string hookName, HookContext context, CancellationToken ct)
    {
        ILifecycleHook hookImpl = hookRegistry.GetHook(hookName)
            ?? throw new InvalidOperationException(
                $"No built-in hook implementation registered for '{hookName}'");

        return await hookImpl.ExecuteAsync(context, ct).ConfigureAwait(false);
    }

    private async Task<HookResult> DispatchCommandAsync(
        ResolvedHook hook, Stopwatch stopwatch, CancellationToken ct)
    {
        try
        {
            // PS: $sb = [scriptblock]::Create($Hook.Value); $output = & $sb 2>&1
            // C#: Run via shell to support piping, redirection, etc.
            ProcessResult result = await processRunner.RunAsync(
                executable: OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                arguments: OperatingSystem.IsWindows()
                    ? ["/c", hook.Command!]
                    : ["-c", hook.Command!],
                timeout: null,
                ct: ct).ConfigureAwait(false);

            stopwatch.Stop();

            return new HookResult(
                Success: result.Success,
                Message: result.Success ? "Command completed" : $"Command failed (exit code {result.ExitCode})",
                Duration: stopwatch.Elapsed,
                Artifacts: [],
                BaseUrl: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new HookResult(
                Success: false,
                Message: $"Command error: {ex.Message}",
                Duration: stopwatch.Elapsed,
                Artifacts: [],
                BaseUrl: null);
        }
    }

    private async Task<HookResult> DispatchHttpAsync(
        ResolvedHook hook, HookContext context, Stopwatch stopwatch, CancellationToken ct)
    {
        // PS: $uri = "$BaseUrl$($Hook.Path)"
        // C#: hook.Url may be relative — combine with context.BaseUrl
        HttpMethod method = new(hook.HttpMethod ?? "GET");
        Uri requestUri;

        if (hook.Url!.IsAbsoluteUri)
        {
            requestUri = hook.Url;
        }
        else if (context.BaseUrl is not null)
        {
            requestUri = new Uri(context.BaseUrl, hook.Url);
        }
        else
        {
            return new HookResult(
                Success: false,
                Message: $"HTTP hook has relative URL '{hook.Url}' but no BaseUrl provided",
                Duration: stopwatch.Elapsed,
                Artifacts: [],
                BaseUrl: null);
        }

        try
        {
            using HttpRequestMessage request = new(method, requestUri);
            using HttpResponseMessage response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
            stopwatch.Stop();

            _ = response.EnsureSuccessStatusCode();

            return new HookResult(
                Success: true,
                Message: $"HTTP {method} {requestUri} succeeded",
                Duration: stopwatch.Elapsed,
                Artifacts: [],
                BaseUrl: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new HookResult(
                Success: false,
                Message: $"HTTP {method} {requestUri} failed: {ex.Message}",
                Duration: stopwatch.Elapsed,
                Artifacts: [],
                BaseUrl: null);
        }
    }
}
