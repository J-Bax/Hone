using FluentAssertions;

using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Diagnostics.Collectors;
using Hone.TestInfrastructure;

using NSubstitute;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Diagnostics.Tests.Collectors;

public sealed class PerfViewGcCollectorTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public async Task Start_UsesGcOnlyMode()
    {
        IProcessRunner runner = Substitute.For<IProcessRunner>();

        // Return immediately to capture args
        _ = runner.RunAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(),
                Arg.Any<string?>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(Success: true, Output: "", ExitCode: 0, TimedOut: false));

        var collector = new PerfViewGcCollector(runner);

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

        // Find the PerfView collect call
        IEnumerable<NSubstitute.Core.ICall> perfViewCalls = runner.ReceivedCalls()
            .Where(c => ((string)c.GetArguments()[0]!).Contains("PerfView", StringComparison.OrdinalIgnoreCase));

        _ = perfViewCalls.Should().NotBeEmpty();

        NSubstitute.Core.ICall collectCall = perfViewCalls.First();
        var args = ((IEnumerable<string>)collectCall.GetArguments()[1]!).ToList();

        // GC collector MUST use /GCOnly and /ClrEvents:GC
        _ = args.Should().Contain("/GCOnly");
        _ = args.Should().Contain("/ClrEvents:GC");

        // GC collector should NOT have CPU-specific flags
        _ = args.Should().NotContain("/StackCompression:false");
        _ = args.Should().NotContain("/DotNetAllocSampled");
        _ = args.Should().NotContain("/ClrEvents:Default");

        // Common flags should still be present
        _ = args.Should().Contain("collect");
        _ = args.Should().Contain("/NoGui");
        _ = args.Should().Contain("/AcceptEULA");
        _ = args.Should().Contain("/Merge:true");
        _ = args.Should().Contain("/Zip:true");
        _ = args.Should().Contain("/focusProcess:1234");
    }

    [Fact]
    public async Task Start_UsesGcSessionNames()
    {
        IProcessRunner runner = Substitute.For<IProcessRunner>();

        _ = runner.RunAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(),
                Arg.Any<string?>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(Success: true, Output: "", ExitCode: 0, TimedOut: false));

        var collector = new PerfViewGcCollector(runner);

        string fakePerfView = Path.Combine(TempDir, "PerfView.exe");
        await File.WriteAllTextAsync(fakePerfView, "");

        var settings = new CollectorSettings(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["PerfViewExePath"] = fakePerfView,
        });

        _ = await collector.StartAsync(1234, TempDir, settings);

        // Verify logman cleans GC-specific session name
        IEnumerable<NSubstitute.Core.ICall> logmanCalls = runner.ReceivedCalls()
            .Where(c => string.Equals((string)c.GetArguments()[0]!, "logman", StringComparison.OrdinalIgnoreCase));

        var sessionNames = logmanCalls
            .Select(c => ((IEnumerable<string>)c.GetArguments()[1]!).Skip(1).First())
            .ToList();

        _ = sessionNames.Should().Contain("PerfViewGCSession");
        _ = sessionNames.Should().Contain("NT Kernel Logger");
    }

    [Fact]
    public async Task Start_OutputFileNamedForGc()
    {
        IProcessRunner runner = Substitute.For<IProcessRunner>();

        _ = runner.RunAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(),
                Arg.Any<string?>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(Success: true, Output: "", ExitCode: 0, TimedOut: false));

        var collector = new PerfViewGcCollector(runner);

        string fakePerfView = Path.Combine(TempDir, "PerfView.exe");
        await File.WriteAllTextAsync(fakePerfView, "");

        var settings = new CollectorSettings(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["PerfViewExePath"] = fakePerfView,
        });

        string outputDir = Path.Combine(TempDir, "output");
        _ = await collector.StartAsync(1234, outputDir, settings);

        // Verify the /DataFile arg uses perfview-gc prefix
        IEnumerable<NSubstitute.Core.ICall> perfViewCalls = runner.ReceivedCalls()
            .Where(c => ((string)c.GetArguments()[0]!).Contains("PerfView", StringComparison.OrdinalIgnoreCase));

        var args = ((IEnumerable<string>)perfViewCalls.First().GetArguments()[1]!).ToList();
        string? dataFileArg = args.FirstOrDefault(a => a.StartsWith("/DataFile:", StringComparison.Ordinal));
        _ = dataFileArg.Should().NotBeNull();
        _ = dataFileArg.Should().Contain("perfview-gc.etl.zip");
    }

    [Fact]
    public async Task Stop_ReturnsFailureForInvalidHandle()
    {
        IProcessRunner runner = Substitute.For<IProcessRunner>();
        var collector = new PerfViewGcCollector(runner);

        CollectorArtifacts result = await collector.StopAsync("invalid-handle");

        _ = result.Success.Should().BeFalse();
        _ = result.ArtifactPaths.Should().BeEmpty();
    }
}
