using System.Text.Json;

using FluentAssertions;

using Hone.Agents.Core;
using Hone.Agents.Loop.Analysis;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.TestInfrastructure;

using NSubstitute;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Agents.Loop.Tests.Analysis;

public sealed class AnalysisAgentTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private readonly IAgentRunner _runner = Substitute.For<IAgentRunner>();

    private AnalysisAgent CreateSut(AgentConfig? config = null)
    {
        AgentInvoker invoker = new(_runner, config ?? new AgentConfig());
        return new AnalysisAgent(invoker);
    }

    private static MetricSet MakeMetrics(
        double p95 = 50.0,
        double rate = 100.0,
        double errorRate = 0.01,
        int experiment = 0) =>
        new(
            Timestamp: "2024-01-01T00:00:00Z",
            Experiment: experiment,
            Run: 1,
            HttpReqDuration: new HttpReqDurationMetrics(Avg: 30, P50: 40, P90: 45, P95: p95, P99: 55, Max: 100),
            HttpReqs: new HttpReqCountMetrics(Count: 1000, Rate: rate),
            HttpReqFailed: new HttpReqFailedMetrics(Count: 10, Rate: errorRate),
            SummaryPath: null);

    private static AnalysisContext MakeContext(
        IReadOnlyList<string>? sourceFiles = null,
        string counterContext = "",
        string trafficContext = "",
        string historyContext = "",
        string profilingContext = "") =>
        new(
            SourceFilePaths: sourceFiles ?? ["Controllers/ValuesController.cs", "Models/Item.cs"],
            CounterContext: counterContext,
            TrafficContext: trafficContext,
            HistoryContext: historyContext,
            ProfilingContext: profilingContext);

    private static string MakeAgentJson(params object[] opportunities) =>
        JsonSerializer.Serialize(new { opportunities });

    private static AgentRunResult OkResult(string json) =>
        new(Success: true, Output: json, TimedOut: false, ExitCode: 0);

    // ── ParsesOpportunities ─────────────────────────────────────────────

    [Fact]
    public async Task AnalysisAgent_ParsesOpportunities()
    {
        string json = MakeAgentJson(
            new
            {
                filePath = "Controllers/ItemsController.cs",
                title = "Cache DB lookups",
                explanation = "The items endpoint queries the DB on every request",
                scope = "narrow",
                rootCause = "Missing cache layer",
                impactEstimate = "~15% p95 improvement",
            },
            new
            {
                filePath = "Data/Repository.cs",
                title = "Batch SQL queries",
                explanation = "N+1 query pattern in GetAll",
                scope = "architecture",
                rootCause = "Lazy loading relationships",
                impactEstimate = "~10% p95 improvement",
            });

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult(json));

        AnalysisAgent sut = CreateSut();

        AnalysisResult result = await sut.AnalyzeAsync(
            MakeContext(sourceFiles:
            [
                "Controllers/ItemsController.cs",
                "Data/Repository.cs",
            ]),
            MakeMetrics(),
            MakeMetrics(),
            comparison: null,
            experiment: 1, targetLabel: "SampleApi", workingDirectory: null);

        _ = result.Success.Should().BeTrue();
        _ = result.Opportunities.Should().HaveCount(2);

        _ = result.Opportunities[0].FilePath.Should().Be("Controllers/ItemsController.cs");
        _ = result.Opportunities[0].Title.Should().Be("Cache DB lookups");
        _ = result.Opportunities[0].Scope.Should().Be(OpportunityScope.Narrow);
        _ = result.Opportunities[0].RootCause.Should().Be("Missing cache layer");
        _ = result.Opportunities[0].ImpactEstimate.Should().Be("~15% p95 improvement");

        _ = result.Opportunities[1].FilePath.Should().Be("Data/Repository.cs");
        _ = result.Opportunities[1].Scope.Should().Be(OpportunityScope.Architecture);
    }

    // ── MockResponse_ExtractsPrimary ────────────────────────────────────

    [Fact]
    public async Task AnalysisAgent_MockResponse_ExtractsPrimary()
    {
        string json = MakeAgentJson(
            new
            {
                filePath = "Services/AuthService.cs",
                title = "Reduce token validation overhead",
                explanation = "JWT validation is synchronous and blocks the request pipeline",
                scope = "narrow",
            },
            new
            {
                filePath = "Middleware/Logging.cs",
                title = "Async logging",
                explanation = "Synchronous file I/O in hot path",
                scope = "narrow",
            });

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult(json));

        AnalysisAgent sut = CreateSut();

        AnalysisResult result = await sut.AnalyzeAsync(
            MakeContext(sourceFiles:
            [
                "Services/AuthService.cs",
                "Middleware/Logging.cs",
            ]),
            MakeMetrics(),
            MakeMetrics(),
            comparison: null,
            experiment: 2, targetLabel: "SampleApi", workingDirectory: null);

        _ = result.Success.Should().BeTrue();
        _ = result.FilePath.Should().Be("Services/AuthService.cs");
        _ = result.Explanation.Should().Be("JWT validation is synchronous and blocks the request pipeline");
    }

    [Fact]
    public async Task AnalysisAgent_CanonicalizesFilePathFromUniqueSuffix()
    {
        string json = MakeAgentJson(
            new
            {
                filePath = "PublicApi/CatalogItemEndpoints/CreateCatalogItemEndpoint.cs",
                title = "Optimize catalog creation",
                explanation = "Endpoint allocates too much per request",
                scope = "narrow",
            });

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult(json));

        AnalysisAgent sut = CreateSut();

        AnalysisResult result = await sut.AnalyzeAsync(
            MakeContext(sourceFiles: ["src/PublicApi/CatalogItemEndpoints/CreateCatalogItemEndpoint.cs"]),
            MakeMetrics(),
            MakeMetrics(),
            comparison: null,
            experiment: 1,
            targetLabel: "eShopOnWeb",
            workingDirectory: null);

        _ = result.FilePath.Should().Be("src/PublicApi/CatalogItemEndpoints/CreateCatalogItemEndpoint.cs");
        _ = result.Opportunities[0].FilePath.Should().Be("src/PublicApi/CatalogItemEndpoints/CreateCatalogItemEndpoint.cs");
    }

    [Fact]
    public async Task AnalysisAgent_AmbiguousSuffixMatch_ThrowsInvalidOperationException()
    {
        string json = MakeAgentJson(
            new
            {
                filePath = "CreateCatalogItemEndpoint.cs",
                title = "Optimize catalog creation",
                explanation = "Endpoint allocates too much per request",
                scope = "narrow",
            });

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult(json));

        AnalysisAgent sut = CreateSut();

        Func<Task> act = () => sut.AnalyzeAsync(
            MakeContext(sourceFiles:
            [
                "src/PublicApi/CatalogItemEndpoints/CreateCatalogItemEndpoint.cs",
                "src/Admin/Catalog/CreateCatalogItemEndpoint.cs",
            ]),
            MakeMetrics(),
            MakeMetrics(),
            comparison: null,
            experiment: 1,
            targetLabel: "eShopOnWeb",
            workingDirectory: null);

        _ = (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ambiguous FilePath*");
    }

    // ── NoOpportunities_ReturnsFailure ──────────────────────────────────

    [Fact]
    public async Task AnalysisAgent_NoOpportunities_ReturnsFailure()
    {
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult("{}"));

        AnalysisAgent sut = CreateSut();

        AnalysisResult result = await sut.AnalyzeAsync(
            MakeContext(sourceFiles: ["Controllers/ItemsController.cs"]),
            MakeMetrics(),
            MakeMetrics(),
            comparison: null,
            experiment: 1, targetLabel: "SampleApi", workingDirectory: null);

        _ = result.Success.Should().BeFalse();
        _ = result.FilePath.Should().BeNull();
        _ = result.Explanation.Should().BeNull();
        _ = result.Opportunities.Should().BeEmpty();
    }

    // ── UsesCorrectModelConfig ──────────────────────────────────────────

    [Fact]
    public async Task AnalysisAgent_UsesCorrectModelConfig()
    {
        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult("{}"));

        AgentConfig config = new(AnalysisModel: "custom-analysis-model");
        AnalysisAgent sut = CreateSut(config);

        _ = await sut.AnalyzeAsync(
            MakeContext(), MakeMetrics(), MakeMetrics(), comparison: null,
            experiment: 1, targetLabel: "SampleApi", workingDirectory: null);

        _ = await _runner.Received(1).InvokeAsync(
            Arg.Is<AgentInvocation>(inv =>
                inv.AgentName == "hone-analyst" &&
                inv.Model == "custom-analysis-model"),
            Arg.Any<CancellationToken>());
    }

    // ── PromptIncludesMetrics ───────────────────────────────────────────

    [Fact]
    public async Task AnalysisAgent_PromptIncludesMetrics()
    {
        AgentInvocation? capturedInvocation = null;

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedInvocation = callInfo.Arg<AgentInvocation>();
                return OkResult("{}");
            });

        MetricSet current = MakeMetrics(p95: 42.5, rate: 250.7, errorRate: 0.025, experiment: 3);
        MetricSet baseline = MakeMetrics(p95: 55.0, rate: 200.3, errorRate: 0.01);
        ComparisonResult comparison = new(
            Accepted: true,
            Outcome: ExperimentOutcome.Improved,
            ImprovementPct: 22.7,
            RegressionPct: 0.0,
            Details: []);

        AnalysisAgent sut = CreateSut();

        AnalysisResult result = await sut.AnalyzeAsync(
            MakeContext(), current, baseline, comparison,
            experiment: 3, targetLabel: "SampleApi", workingDirectory: null);

        _ = capturedInvocation.Should().NotBeNull();
        string prompt = capturedInvocation!.Prompt;

        // Current metrics
        _ = prompt.Should().Contain("Experiment 3");
        _ = prompt.Should().Contain("42.5ms");
        _ = prompt.Should().Contain("250.7");
        _ = prompt.Should().Contain("2.5%");

        // Baseline metrics
        _ = prompt.Should().Contain("55ms");
        _ = prompt.Should().Contain("200.3");
        _ = prompt.Should().Contain("1%");

        // Improvement
        _ = prompt.Should().Contain("22.7%");

        // Source files
        _ = prompt.Should().Contain("Controllers/ValuesController.cs");
        _ = prompt.Should().Contain("Models/Item.cs");

        // JSON instruction
        _ = prompt.Should().Contain("Respond with JSON only");

        // Result prompt saved
        _ = result.Prompt.Should().Be(prompt);
    }

    // ── Scope defaults to Narrow ────────────────────────────────────────

    [Fact]
    public async Task AnalysisAgent_MissingScope_ThrowsInvalidOperationException()
    {
        string json = MakeAgentJson(
            new
            {
                filePath = "Controllers/ItemsController.cs",
                title = "Optimize endpoint",
                explanation = "Slow query",
            });

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult(json));

        AnalysisAgent sut = CreateSut();

        Func<Task> act = () => sut.AnalyzeAsync(
            MakeContext(), MakeMetrics(), MakeMetrics(), comparison: null,
            experiment: 1, targetLabel: "SampleApi", workingDirectory: null);

        _ = await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Title falls back to explanation ──────────────────────────────────

    [Fact]
    public async Task AnalysisAgent_MissingTitle_FallsBackToExplanation()
    {
        string json = MakeAgentJson(
            new
            {
                filePath = "Controllers/ItemsController.cs",
                explanation = "Slow DB queries cause high latency",
                scope = "narrow",
            });

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult(json));

        AnalysisAgent sut = CreateSut();

        AnalysisResult result = await sut.AnalyzeAsync(
            MakeContext(sourceFiles: ["Controllers/ItemsController.cs"]),
            MakeMetrics(),
            MakeMetrics(),
            comparison: null,
            experiment: 1, targetLabel: "SampleApi", workingDirectory: null);

        _ = result.Opportunities[0].Title.Should().Be("Slow DB queries cause high latency");
        _ = result.Opportunities[0].Explanation.Should().Be("Slow DB queries cause high latency");
    }

    // ── Null comparison uses 0 improvement ──────────────────────────────

    [Fact]
    public async Task AnalysisAgent_NullComparison_UsesZeroImprovement()
    {
        AgentInvocation? capturedInvocation = null;

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedInvocation = callInfo.Arg<AgentInvocation>();
                return OkResult("{}");
            });

        AnalysisAgent sut = CreateSut();

        _ = await sut.AnalyzeAsync(
            MakeContext(), MakeMetrics(), MakeMetrics(), comparison: null,
            experiment: 1, targetLabel: "SampleApi", workingDirectory: null);

        _ = capturedInvocation!.Prompt.Should().Contain("Improvement vs baseline: 0%");
    }

    // ── EmptyOpportunitiesList_ReturnsFailure ───────────────────────────

    [Fact]
    public async Task AnalysisAgent_EmptyOpportunitiesList_ReturnsFailure()
    {
        string json = JsonSerializer.Serialize(new { opportunities = Array.Empty<object>() });

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult(json));

        AnalysisAgent sut = CreateSut();

        AnalysisResult result = await sut.AnalyzeAsync(
            MakeContext(), MakeMetrics(), MakeMetrics(), comparison: null,
            experiment: 1, targetLabel: "SampleApi", workingDirectory: null);

        _ = result.Success.Should().BeFalse();
        _ = result.FilePath.Should().BeNull();
        _ = result.Explanation.Should().BeNull();
        _ = result.Opportunities.Should().BeEmpty();
    }

    // ── MissingFilePath_ThrowsInvalidOperationException ─────────────────

    [Fact]
    public async Task AnalysisAgent_MissingFilePath_ThrowsInvalidOperationException()
    {
        string json = MakeAgentJson(
            new
            {
                title = "Optimize endpoint",
                explanation = "Slow query",
                scope = "narrow",
            });

        _ = _runner.InvokeAsync(Arg.Any<AgentInvocation>(), Arg.Any<CancellationToken>())
            .Returns(OkResult(json));

        AnalysisAgent sut = CreateSut();

        Func<Task> act = () => sut.AnalyzeAsync(
            MakeContext(), MakeMetrics(), MakeMetrics(), comparison: null,
            experiment: 1, targetLabel: "SampleApi", workingDirectory: null);

        _ = await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
