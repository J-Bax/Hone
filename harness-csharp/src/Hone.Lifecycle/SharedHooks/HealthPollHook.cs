using System.Diagnostics;
using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Lifecycle.SharedHooks;

/// <summary>
/// Built-in hook that polls a health endpoint until healthy or timeout.
/// Replaces <c>hooks/health-poll.ps1</c>.
/// </summary>
public sealed class HealthPollHook(HttpClient httpClient) : ILifecycleHook
{
    /// <summary>
    /// Default polling interval between health check attempts.
    /// </summary>
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    public async Task<HookResult> ExecuteAsync(HookContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stopwatch = Stopwatch.StartNew();

        // PS parity: if no BaseUrl, fail immediately
        if (context.BaseUrl is null)
        {
            stopwatch.Stop();
            return new HookResult(
                Success: false,
                Message: "No BaseUrl provided",
                Duration: stopwatch.Elapsed,
                Artifacts: [],
                BaseUrl: null);
        }

        // PS: $healthUrl = "$BaseUrl$($Config.Api.HealthEndpoint)"
        var healthUrl = new Uri(context.BaseUrl, context.Config.Api.HealthEndpoint);
        int timeoutSeconds = context.Config.Api.StartupTimeout;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        bool healthy = await PollUntilHealthyAsync(healthUrl, timeout, DefaultPollInterval, ct)
            .ConfigureAwait(false);

        stopwatch.Stop();

        return new HookResult(
            Success: healthy,
            Message: healthy
                ? $"Health endpoint healthy after {stopwatch.Elapsed.TotalSeconds:F1}s"
                : $"Health endpoint not healthy after {timeoutSeconds}s",
            Duration: stopwatch.Elapsed,
            Artifacts: [],
            BaseUrl: null);
    }

    /// <summary>
    /// Polls the health URL until a successful HTTP response or timeout.
    /// </summary>
    internal async Task<bool> PollUntilHealthyAsync(
        Uri healthUrl, TimeSpan timeout, TimeSpan interval, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using HttpResponseMessage response = await httpClient.GetAsync(healthUrl, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
                // Connection refused or other HTTP error — keep polling
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // HttpClient timeout (not user cancellation) — keep polling
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }

        return false;
    }
}
