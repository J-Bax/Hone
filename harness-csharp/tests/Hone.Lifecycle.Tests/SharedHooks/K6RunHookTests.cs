using FluentAssertions;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Lifecycle.SharedHooks;
using Hone.TestInfrastructure;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Lifecycle.Tests.SharedHooks;

public sealed class K6RunHookTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private static readonly Uri TestBaseUrl = new("http://localhost:5000");

    private readonly ILoadTestRunner _loadTestRunner = Substitute.For<ILoadTestRunner>();

    private K6RunHook CreateSut() => new(_loadTestRunner);

    private HookContext CreateContext(Uri? baseUrl = null, int experiment = 1)
    {
        string targetPath = CreateTargetDir("target");
        return new HookContext(
            TargetPath: targetPath,
            Config: new HoneConfig(),
            BaseUrl: baseUrl ?? TestBaseUrl,
            Experiment: experiment);
    }

    private void SetupLoadTestRunner(bool success, string? summaryPath = null, string? testOutput = null)
    {
        _ = _loadTestRunner.RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LoadTestResult(
                Success: success,
                Metrics: null,
                SummaryPath: summaryPath,
                Output: testOutput));
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulRun_ReturnsSuccess()
    {
        SetupLoadTestRunner(success: true);
        HookContext context = CreateContext();
        K6RunHook sut = CreateSut();

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Success.Should().BeTrue();
        _ = result.Message.Should().Be("k6 scale tests completed");
        _ = result.Duration.Should().BePositive();
    }

    [Fact]
    public async Task ExecuteAsync_FailedRun_ReturnsFailure()
    {
        SetupLoadTestRunner(success: false);
        HookContext context = CreateContext();
        K6RunHook sut = CreateSut();

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Success.Should().BeFalse();
        _ = result.Message.Should().Be("k6 scale tests failed");
    }

    [Fact]
    public async Task ExecuteAsync_NoBaseUrl_ReturnsFailure()
    {
        HookContext context = CreateContext(baseUrl: null);
        // Override BaseUrl to null (CreateContext sets it)
        context = context with { BaseUrl = null };
        K6RunHook sut = CreateSut();

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Success.Should().BeFalse();
        _ = result.Message.Should().Be("k6 scale tests require a BaseUrl");
        _ = result.Artifacts.Should().BeEmpty();
        _ = _loadTestRunner.DidNotReceive().RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithSummaryPath_IncludesInArtifacts()
    {
        SetupLoadTestRunner(success: true, summaryPath: "/results/summary.json");
        HookContext context = CreateContext();
        K6RunHook sut = CreateSut();

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Artifacts.Should().ContainSingle()
            .Which.Should().Be("/results/summary.json");
    }

    [Fact]
    public async Task ExecuteAsync_LoadTestThrows_ReturnsError()
    {
        _ = _loadTestRunner.RunAsync(Arg.Any<LoadTestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("k6 binary not found"));
        HookContext context = CreateContext();
        K6RunHook sut = CreateSut();

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Success.Should().BeFalse();
        _ = result.Message.Should().Be("k6 scale tests error: k6 binary not found");
        _ = result.Artifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ConstructsCorrectOptions()
    {
        SetupLoadTestRunner(success: true);
        HookContext context = CreateContext(experiment: 5);
        K6RunHook sut = CreateSut();

        _ = await sut.ExecuteAsync(context);

        _ = await _loadTestRunner.Received(1).RunAsync(
            Arg.Is<LoadTestOptions>(opts =>
                opts.ScenarioPath.EndsWith("baseline.js", StringComparison.Ordinal)
                && opts.OutputDir.EndsWith("experiment-5", StringComparison.Ordinal)
                && opts.BaseUrl == TestBaseUrl
                && opts.Experiment == 5
                && opts.Run == 1
                && opts.Timeout == null),
            Arg.Any<CancellationToken>());
    }
}
