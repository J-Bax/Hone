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

public sealed class DotnetTestHookTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();

    private DotnetTestHook CreateSut() => new(_processRunner);

    private HookContext CreateContext(int experiment = 1)
    {
        string targetPath = CreateTargetDir("target");
        return new HookContext(
            TargetPath: targetPath,
            Config: new HoneConfig(),
            BaseUrl: null,
            Experiment: experiment);
    }

    private static string TestOutputWithCounts(int total, int passed, int failed) =>
        $"""
         Test run summary:
           Total tests: {total}
           Passed: {passed}
           Failed: {failed}
         """;

    private void SetupProcessRunner(bool success, string testOutput)
    {
        _ = _processRunner.RunAsync(
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string?>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(
                Success: success,
                Output: testOutput,
                ExitCode: success ? 0 : 1,
                TimedOut: false));
    }

    [Fact]
    public async Task ExecuteAsync_TestsPass_ReturnsSuccess()
    {
        SetupProcessRunner(success: true, TestOutputWithCounts(total: 10, passed: 10, failed: 0));
        HookContext context = CreateContext();
        DotnetTestHook sut = CreateSut();

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Success.Should().BeTrue();
        _ = result.Message.Should().Be("10/10 tests passed");
        _ = result.Duration.Should().BePositive();
    }

    [Fact]
    public async Task ExecuteAsync_TestsFail_ReturnsFailure()
    {
        SetupProcessRunner(success: false, TestOutputWithCounts(total: 10, passed: 7, failed: 3));
        HookContext context = CreateContext();
        DotnetTestHook sut = CreateSut();

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Success.Should().BeFalse();
        _ = result.Message.Should().Be("3/10 tests FAILED");
    }

    [Fact]
    public async Task ExecuteAsync_ParsesTestCounts_FromOutput()
    {
        string testOutput = """
            Starting test execution...
            Total tests: 42
            Passed: 40
            Failed: 2
            Skipped: 0
            """;
        SetupProcessRunner(success: false, testOutput);
        HookContext context = CreateContext();
        DotnetTestHook sut = CreateSut();

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Message.Should().Be("2/42 tests FAILED");
    }

    [Fact]
    public async Task ExecuteAsync_SavesTestOutputAndTrx()
    {
        SetupProcessRunner(success: true, TestOutputWithCounts(total: 5, passed: 5, failed: 0));
        HookContext context = CreateContext(experiment: 2);
        DotnetTestHook sut = CreateSut();

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Artifacts.Should().HaveCount(2);
        _ = result.Artifacts[0].Should().EndWith("e2e-results.trx");
        _ = result.Artifacts[1].Should().EndWith("e2e-tests.log");
        _ = File.Exists(result.Artifacts[1]).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_NoTestCountsInOutput_DefaultsToZero()
    {
        SetupProcessRunner(success: true, "No test results here");
        HookContext context = CreateContext();
        DotnetTestHook sut = CreateSut();

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Success.Should().BeTrue();
        _ = result.Message.Should().Be("0/0 tests passed");
    }
}
