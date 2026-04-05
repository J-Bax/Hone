using FluentAssertions;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Lifecycle.SharedHooks;
using Hone.TestInfrastructure;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Lifecycle.Tests.SharedHooks;

public sealed class DotnetBuildHookTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();

    private DotnetBuildHook CreateSut() => new(_processRunner);

    private HookContext CreateContext(int experiment = 1)
    {
        string targetPath = CreateTargetDir("target");
        return new HookContext(
            TargetPath: targetPath,
            Config: new HoneConfig(),
            BaseUrl: null,
            Experiment: experiment);
    }

    private void SetupProcessRunner(bool success, int exitCode = 0, string output = "Build output")
    {
        _ = _processRunner.RunAsync(
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string?>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(Success: success, Output: output, ExitCode: exitCode, TimedOut: false));
    }

    [Fact]
    public async Task ExecuteAsync_BuildSucceeds_ReturnsSuccess()
    {
        SetupProcessRunner(success: true);
        HookContext context = CreateContext();
        DotnetBuildHook sut = CreateSut();

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Success.Should().BeTrue();
        _ = result.Message.Should().Be("Build succeeded");
        _ = result.Duration.Should().BePositive();
    }

    [Fact]
    public async Task ExecuteAsync_BuildFails_ReturnsFailure()
    {
        SetupProcessRunner(success: false, exitCode: 1);
        HookContext context = CreateContext();
        DotnetBuildHook sut = CreateSut();

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Success.Should().BeFalse();
        _ = result.Message.Should().Be("Build failed (exit code 1)");
    }

    [Fact]
    public async Task ExecuteAsync_WithExperiment_SavesBuildLog()
    {
        SetupProcessRunner(success: true, output: "MSBuild complete");
        HookContext context = CreateContext(experiment: 3);
        DotnetBuildHook sut = CreateSut();

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Artifacts.Should().HaveCount(1);
        string buildLogPath = result.Artifacts[0];
        _ = buildLogPath.Should().EndWith("build.log");
        _ = File.Exists(buildLogPath).Should().BeTrue();
        _ = (await File.ReadAllTextAsync(buildLogPath)).Should().Be("MSBuild complete");
    }

    [Fact]
    public async Task ExecuteAsync_NoExperiment_SkipsLogFile()
    {
        SetupProcessRunner(success: true);
        HookContext context = CreateContext(experiment: 0);
        DotnetBuildHook sut = CreateSut();

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Success.Should().BeTrue();
        _ = result.Artifacts.Should().BeEmpty();
    }
}
