using FluentAssertions;
using Hone.Core.Contracts;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Agents.CopilotCli.Tests;

public sealed class CopilotCliAgentRunnerTests(ITestOutputHelper output) : HoneTestBase(output)
{
    // ---------------------------------------------------------------
    // Argument-building (unit, no process spawned)
    // ---------------------------------------------------------------

    [Fact]
    public void BuildArguments_BasicInvocation_ContainsExpectedFlags()
    {
        var invocation = new AgentInvocation("my-agent", "do stuff", Model: "gpt-4o");

        List<string> args = CopilotCliAgentRunner.BuildArguments(invocation);

        _ = args.Should().ContainInOrder("--agent", "my-agent");
        _ = args.Should().ContainInOrder("--model", "gpt-4o");
        _ = args.Should().ContainInOrder("-p", "do stuff");
        _ = args.Should().Contain("-s");
        _ = args.Should().Contain("--no-auto-update");
        _ = args.Should().Contain("--no-ask-user");
        _ = args.Should().NotContain("--no-custom-instructions");
    }

    [Fact]
    public void BuildArguments_WorkingDirectory_AddsNoCustomInstructions()
    {
        var invocation = new AgentInvocation(
            "my-agent", "do stuff", Model: "gpt-4o", WorkingDirectory: @"C:\some\dir");

        List<string> args = CopilotCliAgentRunner.BuildArguments(invocation);

        _ = args.Should().Contain("--no-custom-instructions");
    }

    [Fact]
    public void BuildArguments_NullModel_UsesDefault()
    {
        var invocation = new AgentInvocation("agent", "prompt");

        List<string> args = CopilotCliAgentRunner.BuildArguments(invocation);

        _ = args.Should().ContainInOrder("--model", "claude-sonnet-4-20250514");
    }

    [Fact]
    public void BuildArguments_PromptWithSpecialCharacters_PreservedVerbatim()
    {
        string prompt = "Fix the \"bug\" in <main> & run tests | grep 'ok'";
        var invocation = new AgentInvocation("agent", prompt, Model: "gpt-4o");

        List<string> args = CopilotCliAgentRunner.BuildArguments(invocation);

        // ArgumentList preserves the string verbatim — no shell escaping needed
        _ = args.Should().ContainInOrder("-p", prompt);
    }

    // ---------------------------------------------------------------
    // Process integration tests (spawn real lightweight processes)
    // ---------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_SuccessfulProcess_ReturnsOutput()
    {
        var runner = new TestableAgentRunner("dotnet", ["--version"]);
        var invocation = new AgentInvocation("test", "ignored");

        AgentRunResult result = await runner.InvokeAsync(invocation);

        _ = result.Success.Should().BeTrue();
        _ = result.ExitCode.Should().Be(0);
        _ = result.TimedOut.Should().BeFalse();
        _ = result.Output.Should().NotBeNullOrWhiteSpace();
        Output.WriteLine($"Output: {result.Output.Trim()}");
    }

    [Fact]
    public async Task InvokeAsync_NonZeroExit_ReturnsFailure()
    {
        var runner = new TestableAgentRunner("dotnet", ["--unknown-flag-xyz"]);
        var invocation = new AgentInvocation("test", "ignored");

        AgentRunResult result = await runner.InvokeAsync(invocation);

        _ = result.Success.Should().BeFalse();
        _ = result.ExitCode.Should().NotBe(0);
        _ = result.TimedOut.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_Timeout_KillsProcess()
    {
        string[] sleepArgs = OperatingSystem.IsWindows()
            ? ["-Command", "Start-Sleep -Seconds 30"]
            : ["-c", "sleep 30"];
        string shell = OperatingSystem.IsWindows()
            ? "powershell" : "sh";

        var runner = new TestableAgentRunner(shell, sleepArgs);
        var invocation = new AgentInvocation(
            "test", "ignored", Timeout: TimeSpan.FromSeconds(2));

        AgentRunResult result = await runner.InvokeAsync(invocation);

        _ = result.TimedOut.Should().BeTrue();
        _ = result.Success.Should().BeFalse();
        _ = result.ExitCode.Should().Be(-1);
    }

    [Fact]
    public async Task InvokeAsync_CancellationToken_KillsProcess()
    {
        string[] sleepArgs = OperatingSystem.IsWindows()
            ? ["-Command", "Start-Sleep -Seconds 30"]
            : ["-c", "sleep 30"];
        string shell = OperatingSystem.IsWindows()
            ? "powershell" : "sh";

        var runner = new TestableAgentRunner(shell, sleepArgs);
        var invocation = new AgentInvocation(
            "test", "ignored", Timeout: TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        AgentRunResult result = await runner.InvokeAsync(invocation, cts.Token);

        _ = result.Success.Should().BeFalse();
        _ = result.ExitCode.Should().Be(-1);
    }

    [Fact]
    public async Task InvokeAsync_Utf8Output_DecodedCorrectly()
    {
        string utf8Text = "héllo wörld 日本語";
        string[] echoArgs = OperatingSystem.IsWindows()
            ? ["-Command", $"[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; Write-Output '{utf8Text}'"]
            : ["-c", $"echo '{utf8Text}'"];
        string shell = OperatingSystem.IsWindows()
            ? "powershell" : "sh";

        var runner = new TestableAgentRunner(shell, echoArgs);
        var invocation = new AgentInvocation("test", "ignored");

        AgentRunResult result = await runner.InvokeAsync(invocation);

        _ = result.Success.Should().BeTrue();
        _ = result.Output.Trim().Should().Be(utf8Text);
    }

    [Fact]
    public async Task InvokeAsync_NullInvocation_ThrowsArgumentNullException()
    {
        var runner = new CopilotCliAgentRunner();
        Func<Task> act = () => runner.InvokeAsync(null!);
        _ = await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ---------------------------------------------------------------
    // Test helper: overrides FileName and ArgumentList for testing
    // ---------------------------------------------------------------

    /// <summary>
    /// A testable wrapper that lets us substitute the executable and args
    /// while exercising the full process-management logic of the runner.
    /// </summary>
    private sealed class TestableAgentRunner(string fileName, string[] fixedArgs) : IAgentRunner
    {
        public async Task<AgentRunResult> InvokeAsync(AgentInvocation invocation, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(invocation);

            TimeSpan timeout = invocation.Timeout ?? TimeSpan.FromSeconds(600);

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };

            foreach (string arg in fixedArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = new System.Diagnostics.Process { StartInfo = startInfo };

            if (!process.Start())
            {
                return new AgentRunResult(
                    Success: false, Output: "Failed to start process",
                    TimedOut: false, ExitCode: -1);
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process);
                string partial = await ReadPartialAsync(stdoutTask).ConfigureAwait(false);
                return new AgentRunResult(
                    Success: false, Output: partial,
                    TimedOut: true, ExitCode: -1);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                string partial = await ReadPartialAsync(stdoutTask).ConfigureAwait(false);
                return new AgentRunResult(
                    Success: false, Output: partial,
                    TimedOut: false, ExitCode: -1);
            }

            string output = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);

            return new AgentRunResult(
                Success: process.ExitCode == 0, Output: output,
                TimedOut: false, ExitCode: process.ExitCode);
        }

        private static void TryKill(System.Diagnostics.Process process)
        {
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
                // Best-effort cleanup
            }
        }

#pragma warning disable CA1031 // best-effort partial output retrieval after timeout/cancel
        private static async Task<string> ReadPartialAsync(Task<string> task)
        {
            try
            {
                return await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
#pragma warning restore CA1031
    }
}
