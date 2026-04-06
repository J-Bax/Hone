using System.Net;
using FluentAssertions;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Lifecycle.Hooks;
using Hone.TestInfrastructure;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Lifecycle.Tests.Hooks;

public sealed class LifecycleHookDispatcherTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private readonly IBuiltInHookRegistry _registry = Substitute.For<IBuiltInHookRegistry>();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly HttpClient _defaultHttpClient = new();

    private static HookContext DefaultContext(Uri? baseUrl = null) =>
        new(TargetPath: "/tmp/target", Config: new(), BaseUrl: baseUrl, Experiment: 1);

    private LifecycleHookDispatcher CreateSut(HttpClient? httpClient = null) =>
        new(_registry, _processRunner, httpClient ?? _defaultHttpClient);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _defaultHttpClient.Dispose();
        }

        base.Dispose(disposing);
    }

    [Fact]
    public async Task Dispatch_BuiltInHook_ExecutesNativeImplementation()
    {
        var expectedResult = new HookResult(
            Success: true,
            Message: "Built-in executed",
            Duration: TimeSpan.FromMilliseconds(42),
            Artifacts: [],
            BaseUrl: null,
            Process: null);

        ILifecycleHook hookImpl = Substitute.For<ILifecycleHook>();
        _ = hookImpl.ExecuteAsync(Arg.Any<HookContext>(), Arg.Any<CancellationToken>())
            .Returns(expectedResult);
        _ = _registry.GetHook("build").Returns(hookImpl);

        LifecycleHookDispatcher sut = CreateSut();
        HookResult result = await sut.DispatchAsync("build", ResolvedHook.BuiltIn(), DefaultContext());

        _ = result.Success.Should().BeTrue();
        _ = result.Message.Should().Be("Built-in executed");
        _ = await hookImpl.Received(1).ExecuteAsync(Arg.Any<HookContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispatch_BuiltInHook_NotRegistered_Throws()
    {
        _ = _registry.GetHook("unknown").Returns((ILifecycleHook?)null);

        LifecycleHookDispatcher sut = CreateSut();
        Func<Task> act = () => sut.DispatchAsync("unknown", ResolvedHook.BuiltIn(), DefaultContext());

        _ = await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No built-in hook implementation registered for 'unknown'*");
    }

    [Fact]
    public async Task Dispatch_CommandHook_ExecutesCommand()
    {
        _ = _processRunner.RunAsync(
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string?>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(Success: true, Output: "ok", ExitCode: 0, TimedOut: false));

        LifecycleHookDispatcher sut = CreateSut();
        HookResult result = await sut.DispatchAsync(
            "build", ResolvedHook.ForCommand("dotnet build"), DefaultContext());

        _ = result.Success.Should().BeTrue();
        _ = result.Message.Should().Be("Command completed");
        _ = result.Duration.Should().BePositive();
    }

    [Fact]
    public async Task Dispatch_CommandHook_FailedCommand_ReturnsFailure()
    {
        _ = _processRunner.RunAsync(
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string?>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(Success: false, Output: "error", ExitCode: 1, TimedOut: false));

        LifecycleHookDispatcher sut = CreateSut();
        HookResult result = await sut.DispatchAsync(
            "build", ResolvedHook.ForCommand("dotnet build"), DefaultContext());

        _ = result.Success.Should().BeFalse();
        _ = result.Message.Should().Be("Command failed (exit code 1)");
    }

    [Fact]
    public async Task Dispatch_HttpHook_MakesRequest()
    {
        using var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        LifecycleHookDispatcher sut = CreateSut(httpClient);

        HookResult result = await sut.DispatchAsync(
            "health",
            ResolvedHook.ForHttp(new Uri("http://localhost:5000/health"), "GET"),
            DefaultContext());

        _ = result.Success.Should().BeTrue();
        _ = result.Message.Should().Contain("succeeded");
    }

    [Fact]
    public async Task Dispatch_HttpHook_FailedRequest_ReturnsFailure()
    {
        using var handler = new MockHttpMessageHandler(HttpStatusCode.InternalServerError);
        using var httpClient = new HttpClient(handler);
        LifecycleHookDispatcher sut = CreateSut(httpClient);

        HookResult result = await sut.DispatchAsync(
            "health",
            ResolvedHook.ForHttp(new Uri("http://localhost:5000/health"), "GET"),
            DefaultContext());

        _ = result.Success.Should().BeFalse();
        _ = result.Message.Should().Contain("failed");
    }

    [Fact]
    public async Task Dispatch_HttpHook_RelativeUrl_CombinesWithBaseUrl()
    {
        using var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        LifecycleHookDispatcher sut = CreateSut(httpClient);

        HookContext context = DefaultContext(baseUrl: new Uri("http://localhost:5000"));
        HookResult result = await sut.DispatchAsync(
            "health",
            ResolvedHook.ForHttp(new Uri("/api/health", UriKind.Relative), "GET"),
            context);

        _ = result.Success.Should().BeTrue();
        _ = result.Message.Should().Contain("http://localhost:5000/api/health");
    }

    [Fact]
    public async Task Dispatch_HttpHook_RelativeUrl_NoBaseUrl_ReturnsFailure()
    {
        LifecycleHookDispatcher sut = CreateSut();

        HookResult result = await sut.DispatchAsync(
            "health",
            ResolvedHook.ForHttp(new Uri("/api/health", UriKind.Relative), "GET"),
            DefaultContext(baseUrl: null));

        _ = result.Success.Should().BeFalse();
        _ = result.Message.Should().Contain("no BaseUrl provided");
    }

    [Fact]
    public async Task Dispatch_SkipHook_ReturnsSuccessImmediately()
    {
        LifecycleHookDispatcher sut = CreateSut();

        HookResult result = await sut.DispatchAsync("teardown", ResolvedHook.Skipped(), DefaultContext());

        _ = result.Success.Should().BeTrue();
        _ = result.Message.Should().Be("Skipped");
        _ = result.Duration.Should().Be(TimeSpan.Zero);
        _ = result.Artifacts.Should().BeEmpty();
    }

    private sealed class MockHttpMessageHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }
}
