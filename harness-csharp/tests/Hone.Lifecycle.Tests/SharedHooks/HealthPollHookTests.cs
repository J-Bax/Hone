using System.Net;
using FluentAssertions;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Lifecycle.SharedHooks;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Lifecycle.Tests.SharedHooks;

public sealed class HealthPollHookTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private static HookContext CreateContext(Uri? baseUrl = null, int startupTimeout = 90) =>
        new(
            TargetPath: "unused",
            Config: new HoneConfig(Api: new ApiConfig(StartupTimeout: startupTimeout)),
            BaseUrl: baseUrl,
            Experiment: 0);

    [Fact]
    public async Task ExecuteAsync_NoBaseUrl_ReturnsFailure()
    {
        using var handler = new SequentialMockHandler();
        using var httpClient = new HttpClient(handler);
        var sut = new HealthPollHook(httpClient);
        HookContext context = CreateContext(baseUrl: null);

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Success.Should().BeFalse();
        _ = result.Message.Should().Be("No BaseUrl provided");
        _ = result.Duration.Should().BePositive();
        _ = result.Artifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_HealthyEndpoint_ReturnsSuccess()
    {
        using var handler = new SequentialMockHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var sut = new HealthPollHook(httpClient);
        HookContext context = CreateContext(baseUrl: new Uri("http://localhost:5000"));

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Success.Should().BeTrue();
        _ = result.Message.Should().MatchRegex(@"Health endpoint healthy after \d+\.\ds");
        _ = result.Duration.Should().BePositive();
        _ = result.Artifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutReached_ReturnsFailure()
    {
        using var handler = new SequentialMockHandler(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable);
        using var httpClient = new HttpClient(handler);
        var sut = new HealthPollHook(httpClient);
        HookContext context = CreateContext(
            baseUrl: new Uri("http://localhost:5000"),
            startupTimeout: 2);

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Success.Should().BeFalse();
        _ = result.Message.Should().Be("Health endpoint not healthy after 2s");
    }

    [Fact]
    public async Task ExecuteAsync_EventuallyHealthy_ReturnsSuccess()
    {
        using var handler = new SequentialMockHandler(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var sut = new HealthPollHook(httpClient);
        HookContext context = CreateContext(
            baseUrl: new Uri("http://localhost:5000"),
            startupTimeout: 30);

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Success.Should().BeTrue();
        _ = result.Message.Should().StartWith("Health endpoint healthy after");
    }

    [Fact]
    public async Task ExecuteAsync_UsesConfiguredTimeout()
    {
        using var handler = new AlwaysFailHandler();
        using var httpClient = new HttpClient(handler);
        var sut = new HealthPollHook(httpClient);
        HookContext context = CreateContext(
            baseUrl: new Uri("http://localhost:5000"),
            startupTimeout: 2);

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Success.Should().BeFalse();
        _ = result.Message.Should().Be("Health endpoint not healthy after 2s");
        _ = result.Duration.TotalSeconds.Should().BeGreaterThanOrEqualTo(2);
    }

    /// <summary>
    /// Handler that returns pre-configured status codes in sequence.
    /// After exhausting the sequence, returns 503.
    /// </summary>
    private sealed class SequentialMockHandler(params HttpStatusCode[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(
                _responses.Count > 0 ? _responses.Dequeue() : HttpStatusCode.ServiceUnavailable));

        protected override void Dispose(bool disposing)
        {
            _responses.Clear();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Handler that always returns 503 Service Unavailable.
    /// </summary>
    private sealed class AlwaysFailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
