using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.Lifecycle.SharedHooks;

/// <summary>
/// Built-in hook that starts a .NET API as a background process.
/// Replaces <c>hooks/dotnet-start.ps1</c>.
/// </summary>
public sealed class DotnetStartHook(HttpClient httpClient) : ILifecycleHook
{
    private const int MaxDynamicPortAttempts = 3;

    public async Task<HookResult> ExecuteAsync(HookContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stopwatch = Stopwatch.StartNew();

        string projectPath = Path.Combine(context.TargetPath, context.Config.Api.ProjectPath);
        Uri baseUrl = context.BaseUrl ?? new Uri(context.Config.Api.BaseUrl);
        int timeout = context.Config.Api.StartupTimeout;
        int configuredPort = baseUrl.Port;
        int maxAttempts = configuredPort == 0 ? MaxDynamicPortAttempts : 1;

        Process? apiProcess = null;
        Uri? actualBaseUrl = null;
        bool healthy = false;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            Uri attemptUrl = baseUrl;
            if (configuredPort == 0)
            {
                int freePort = FindFreePort();
                attemptUrl = new Uri($"http://localhost:{freePort}");
            }

            apiProcess = StartDotnetProcess(projectPath, attemptUrl);

            try
            {
                var healthPoll = new HealthPollHook(httpClient);
                HookContext healthContext = context with { BaseUrl = attemptUrl };
                HookResult healthResult = await healthPoll.ExecuteAsync(healthContext, ct)
                    .ConfigureAwait(false);

                if (healthResult.Success)
                {
                    healthy = true;
                    actualBaseUrl = attemptUrl;
                    break;
                }
            }
            catch
            {
                TryKillProcess(apiProcess);
                throw;
            }

            // Not healthy — kill and retry if dynamic port
            TryKillProcess(apiProcess);
            apiProcess = null;

            if (configuredPort != 0)
            {
                break;
            }
        }

        stopwatch.Stop();

        if (healthy && apiProcess is not null)
        {
            TrySetHighPriority(apiProcess);

            return new HookResult(
                Success: true,
                Message: $"API is healthy at {actualBaseUrl} (PID {apiProcess.Id})",
                Duration: stopwatch.Elapsed,
                Artifacts: [],
                BaseUrl: actualBaseUrl);
        }

        TryKillProcess(apiProcess);

        return new HookResult(
            Success: false,
            Message: $"API failed to become healthy within {timeout}s",
            Duration: stopwatch.Elapsed,
            Artifacts: [],
            BaseUrl: null);
    }

    /// <summary>
    /// Finds an available TCP port by briefly binding to port 0.
    /// </summary>
    internal static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Process StartDotnetProcess(string projectPath, Uri baseUrl)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(baseUrl.ToString());

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start dotnet process");
    }

    private static void TrySetHighPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.High;
        }
        catch (InvalidOperationException)
        {
            // Process may have exited before we could set priority
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Insufficient privileges — PS parity: silently ignored
        }
    }

    private static void TryKillProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best-effort cleanup — access denied
        }
        finally
        {
            process.Dispose();
        }
    }
}
