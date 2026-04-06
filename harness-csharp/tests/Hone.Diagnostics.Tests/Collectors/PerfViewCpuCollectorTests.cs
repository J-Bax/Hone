using FluentAssertions;

using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Diagnostics.Collectors;
using Hone.TestInfrastructure;

using NSubstitute;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Diagnostics.Tests.Collectors;

public sealed class PerfViewCpuCollectorTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public async Task Start_BuildsCorrectArguments()
    {
        IProcessRunner runner = Substitute.For<IProcessRunner>();

        // Return immediately to simulate premature exit (we just want to capture args)
        _ = runner.RunAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(),
                Arg.Any<string?>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(Success: true, Output: "", ExitCode: 0, TimedOut: false));

        var collector = new PerfViewCpuCollector(runner);

        string fakePerfView = Path.Combine(TempDir, "PerfView.exe");
        await File.WriteAllTextAsync(fakePerfView, "");

        var settings = new CollectorSettings(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["PerfViewExePath"] = fakePerfView,
            ["MaxCollectSec"] = 90,
            ["BufferSizeMB"] = 256,
        });

        string outputDir = Path.Combine(TempDir, "output");

        _ = await collector.StartAsync(1234, outputDir, settings);

        // Find the PerfView collect call (not logman calls)
        System.Collections.Generic.IEnumerable<NSubstitute.Core.ICall> perfViewCalls = runner.ReceivedCalls()
            .Where(c => ((string)c.GetArguments()[0]!).Contains("PerfView", StringComparison.OrdinalIgnoreCase));

        _ = perfViewCalls.Should().NotBeEmpty("PerfView should have been called");

        NSubstitute.Core.ICall collectCall = perfViewCalls.First();
        var args = ((IEnumerable<string>)collectCall.GetArguments()[1]!).ToList();

        _ = args.Should().Contain("collect");
        _ = args.Should().Contain("/NoGui");
        _ = args.Should().Contain("/AcceptEULA");
        _ = args.Should().Contain("/Merge:true");
        _ = args.Should().Contain("/Zip:true");
        _ = args.Should().Contain("/NoNGenPdbs");
        _ = args.Should().Contain("/StackCompression:false");
        _ = args.Should().Contain("/ClrEvents:Default");
        _ = args.Should().Contain("/DotNetAllocSampled");
        _ = args.Should().Contain("/MaxCollectSec:90");
        _ = args.Should().Contain("/BufferSizeMB:256");
        _ = args.Should().Contain("/focusProcess:1234");

        // CPU collector should NOT use /GCOnly
        _ = args.Should().NotContain("/GCOnly");
    }

    [Fact]
    public async Task Start_FailsWhenPerfViewExeNotConfigured()
    {
        IProcessRunner runner = Substitute.For<IProcessRunner>();
        var collector = new PerfViewCpuCollector(runner);

        var settings = new CollectorSettings(new Dictionary<string, object?>(StringComparer.Ordinal));
        string outputDir = Path.Combine(TempDir, "output");

        CollectorStartResult result = await collector.StartAsync(1234, outputDir, settings);

        _ = result.Success.Should().BeFalse();
        _ = result.Error.Should().Contain("PerfViewExePath");
    }

    [Fact]
    public async Task Start_FailsWhenPerfViewExeNotFound()
    {
        IProcessRunner runner = Substitute.For<IProcessRunner>();
        var collector = new PerfViewCpuCollector(runner);

        var settings = new CollectorSettings(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["PerfViewExePath"] = @"C:\nonexistent\PerfView.exe",
        });
        string outputDir = Path.Combine(TempDir, "output");

        CollectorStartResult result = await collector.StartAsync(1234, outputDir, settings);

        _ = result.Success.Should().BeFalse();
        _ = result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Start_ReportsPrematureExit()
    {
        IProcessRunner runner = Substitute.For<IProcessRunner>();

        // Simulate PerfView exiting immediately with error
        _ = runner.RunAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(),
                Arg.Any<string?>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(Success: false, Output: "error", ExitCode: 1, TimedOut: false));

        var collector = new PerfViewCpuCollector(runner);

        string fakePerfView = Path.Combine(TempDir, "PerfView.exe");
        await File.WriteAllTextAsync(fakePerfView, "");

        var settings = new CollectorSettings(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["PerfViewExePath"] = fakePerfView,
        });
        string outputDir = Path.Combine(TempDir, "output");

        CollectorStartResult result = await collector.StartAsync(1234, outputDir, settings);

        _ = result.Success.Should().BeFalse();
        _ = result.Error.Should().Contain("prematurely");
    }

    [Fact]
    public async Task Start_CleansStaleEtwSessions()
    {
        IProcessRunner runner = Substitute.For<IProcessRunner>();

        _ = runner.RunAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(),
                Arg.Any<string?>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(Success: true, Output: "", ExitCode: 0, TimedOut: false));

        var collector = new PerfViewCpuCollector(runner);

        string fakePerfView = Path.Combine(TempDir, "PerfView.exe");
        await File.WriteAllTextAsync(fakePerfView, "");

        var settings = new CollectorSettings(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["PerfViewExePath"] = fakePerfView,
        });

        _ = await collector.StartAsync(1234, TempDir, settings);

        // Verify logman was called for ETW session cleanup
        IEnumerable<NSubstitute.Core.ICall> logmanCalls = runner.ReceivedCalls()
            .Where(c => string.Equals((string)c.GetArguments()[0]!, "logman", StringComparison.OrdinalIgnoreCase));

        _ = logmanCalls.Should().HaveCount(2, "should clean NT Kernel Logger and PerfViewSession");
    }

    [Fact]
    public async Task Stop_ReturnsFailureForInvalidHandle()
    {
        IProcessRunner runner = Substitute.For<IProcessRunner>();
        var collector = new PerfViewCpuCollector(runner);

        CollectorArtifacts result = await collector.StopAsync("invalid-handle");

        _ = result.Success.Should().BeFalse();
        _ = result.ArtifactPaths.Should().BeEmpty();
    }
}
