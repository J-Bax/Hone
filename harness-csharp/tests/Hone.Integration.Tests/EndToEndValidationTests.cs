using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Measurement.Comparison;
using Hone.Orchestration.Loop;
using Hone.Orchestration.Queue;
using Hone.Reporting.PullRequest;
using Hone.Reporting.Rca;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Integration.Tests;

/// <summary>
/// E2E validation tests that exercise the full pipeline
/// with real YAML config files, real comparison logic, and the integration
/// test harness.
/// </summary>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit requires public test classes")]
public sealed class EndToEndValidationTests(ITestOutputHelper output) : IntegrationTestBase(output)
{
    // ── Path resolution ───────────────────────────────────────────────────────

    /// <summary>
    /// Walks up from the test assembly directory to find the harness-csharp root
    /// (the directory containing Hone.slnx).
    /// </summary>
    private static string FindHarnessCSharpRoot()
    {
        string? dir = AppContext.BaseDirectory;

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Hone.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            "Cannot find harness-csharp root directory (Hone.slnx not found). " +
            $"Searched from: {AppContext.BaseDirectory}");
    }

    // ── 1. Config merge from YAML ─────────────────────────────────────────────

    /// <summary>
    /// Loads the engine config.yaml and the sample-api target config.yaml, merges them,
    /// and verifies that target overrides win for api/scaletest sections while engine
    /// defaults survive for unoverridden sections (tolerances, agents, diagnostics).
    /// </summary>
    [Fact]
    public void ConfigMergeFromYaml_EngineAndTarget_ProducesCorrectMerge()
    {
        string enginePath = Path.Combine(FindHarnessCSharpRoot(), "config.yaml");
        string targetPath = Path.Combine(TestFixturesRootPath, "sample-api-target", ".hone", "config.yaml");

        HoneConfig engine = ConfigLoader.Load(enginePath);
        HoneConfig target = ConfigLoader.Load(targetPath);

        HoneConfig merged = ConfigMerger.Merge(engine, target);

        // Target overrides win — paths are relative to the target project directory
        _ = merged.Api.SolutionPath.Should().Be("SampleApi.sln",
            "target config defines a project-relative SolutionPath");
        _ = merged.Api.ProjectPath.Should().Be("SampleApi",
            "target config defines a project-relative ProjectPath");
        _ = merged.Api.TestProjectPath.Should().Be("SampleApi.Tests",
            "target config defines a project-relative TestProjectPath");

        // Engine defaults survive for sections not overridden by the target
        _ = merged.Tolerances.MaxRegressionPct.Should().Be(engine.Tolerances.MaxRegressionPct,
            "tolerances are not overridden by sample-api target config");
        _ = merged.Tolerances.MinImprovementPct.Should().Be(engine.Tolerances.MinImprovementPct);
        _ = merged.Tolerances.StaleExperimentsBeforeStop.Should().Be(engine.Tolerances.StaleExperimentsBeforeStop);

        _ = merged.Agents.DefaultModel.Should().Be(engine.Agents.DefaultModel,
            "agents section is not overridden by sample-api target config");
        _ = merged.Agents.AnalysisModel.Should().Be(engine.Agents.AnalysisModel);
        _ = merged.Agents.ImplementerModel.Should().Be(engine.Agents.ImplementerModel);

        _ = merged.Diagnostics.Enabled.Should().Be(engine.Diagnostics.Enabled,
            "diagnostics section is not overridden by sample-api target config");
        _ = merged.Diagnostics.CollectorSettings.Should().BeEquivalentTo(engine.Diagnostics.CollectorSettings);

        // Loop defaults also survive
        _ = merged.Loop.MaxExperiments.Should().Be(engine.Loop.MaxExperiments);
        _ = merged.Loop.StackedDiffs.Should().Be(engine.Loop.StackedDiffs);
    }

    // ── 2. Metric comparison snapshot ────────────────────────────────────────

    /// <summary>
    /// Builds baseline + experiment MetricSets with known values, runs them through
    /// MetricComparer.Compare(), and verifies the ComparisonResult structure and
    /// key decision fields.
    /// </summary>
    [Fact]
    public void MetricComparison_SnapshotTest_MatchesExpected()
    {
        var tolerances = new TolerancesConfig(
            MaxRegressionPct: 0.10,
            MinAbsoluteP95DeltaMs: 5,
            MinAbsoluteRPSDelta: 5,
            MinAbsoluteErrorRateDelta: 0.005,
            MinImprovementPct: 0,
            StaleExperimentsBeforeStop: 2,
            MaxConsecutiveFailures: 10);

        var baseline = new MetricSet(
            Timestamp: "2024-01-01T00:00:00Z",
            Experiment: 0,
            Run: 1,
            HttpReqDuration: new HttpReqDurationMetrics(
                Avg: 100, P50: 90, P90: 140, P95: 150, P99: 180, Max: 250),
            HttpReqs: new HttpReqCountMetrics(Count: 10000, Rate: 500),
            HttpReqFailed: new HttpReqFailedMetrics(Count: 0, Rate: 0),
            SummaryPath: null);

        // Experiment shows clear improvement: p95 150→120 (-20%), rps 500→550 (+10%)
        var experiment = new MetricSet(
            Timestamp: "2024-01-01T01:00:00Z",
            Experiment: 1,
            Run: 1,
            HttpReqDuration: new HttpReqDurationMetrics(
                Avg: 80, P50: 72, P90: 112, P95: 120, P99: 145, Max: 200),
            HttpReqs: new HttpReqCountMetrics(Count: 11000, Rate: 550),
            HttpReqFailed: new HttpReqFailedMetrics(Count: 0, Rate: 0),
            SummaryPath: null);

        ComparisonResult result = MetricComparer.Compare(
            current: experiment,
            previous: baseline,
            baseline: null,
            tolerances: tolerances);

        // Outcome assertions
        _ = result.Accepted.Should().BeTrue("p95 improved by 20% and rps improved by 10%");
        _ = result.Outcome.Should().Be(ExperimentOutcome.Improved);
        _ = result.RegressionPct.Should().Be(0, "no regression detected");
        _ = result.ImprovementPct.Should().BeGreaterThan(0, "improvement vs baseline is positive");

        // Per-metric details
        _ = result.Details.Should().HaveCount(3);

        MetricComparison p95Detail = result.Details.Single(d =>
            string.Equals(d.MetricName, "p95", StringComparison.Ordinal));
        _ = p95Detail.Improved.Should().BeTrue();
        _ = p95Detail.Regressed.Should().BeFalse();
        _ = p95Detail.Current.Should().Be(120);
        _ = p95Detail.Previous.Should().Be(150);

        MetricComparison rpsDetail = result.Details.Single(d =>
            string.Equals(d.MetricName, "rps", StringComparison.Ordinal));
        _ = rpsDetail.Improved.Should().BeTrue();
        _ = rpsDetail.Regressed.Should().BeFalse();

        MetricComparison errDetail = result.Details.Single(d =>
            string.Equals(d.MetricName, "error_rate", StringComparison.Ordinal));
        _ = errDetail.Improved.Should().BeFalse("error rate is 0 on both sides — no improvement");
        _ = errDetail.Regressed.Should().BeFalse();

        // Serialization round-trip: key fields survive JSON serialization
        string json = JsonSerializer.Serialize(result);
        _ = json.Should().Contain("\"Accepted\":true");
        _ = json.Should().Contain("\"Outcome\":");
    }

    // ── 3. Queue state lifecycle sequence ────────────────────────────────────

    /// <summary>
    /// Creates a queue with 3 opportunities, consumes all three via GetNext, then
    /// marks each done with a different outcome. Verifies the resulting JSON contains
    /// expected entries with correct statuses.
    /// </summary>
    [Fact]
    public void QueueState_InitConsumeMarkDone_CorrectJsonSequence()
    {
        string metadataDir = CreateTargetDir("queue-e2e");
        IHoneEventSink sink = Substitute.For<IHoneEventSink>();
        var mgr = new OptimizationQueueManager(metadataDir, sink);

        var opportunities = new List<Opportunity>
        {
            new(FilePath: "src/Alpha.cs", Title: "Alpha opt", Explanation: "Alpha explanation",
                Scope: OpportunityScope.Narrow, RootCause: null, ImpactEstimate: null),
            new(FilePath: "src/Beta.cs", Title: "Beta opt", Explanation: "Beta explanation",
                Scope: OpportunityScope.Narrow, RootCause: null, ImpactEstimate: null),
            new(FilePath: "src/Gamma.cs", Title: "Gamma opt", Explanation: "Gamma explanation",
                Scope: OpportunityScope.Narrow, RootCause: null, ImpactEstimate: null),
        };

        // Init
        InitializeResult initResult = mgr.Initialize(opportunities, experiment: 0);
        _ = initResult.Success.Should().BeTrue();
        _ = initResult.Count.Should().Be(3);

        // Consume all three items
        QueueItem? item1 = mgr.GetNext(experiment: 1);
        QueueItem? item2 = mgr.GetNext(experiment: 2);
        QueueItem? item3 = mgr.GetNext(experiment: 3);

        _ = item1.Should().NotBeNull();
        _ = item2.Should().NotBeNull();
        _ = item3.Should().NotBeNull();
        _ = item1.FilePath.Should().Be("src/Alpha.cs");
        _ = item2.FilePath.Should().Be("src/Beta.cs");
        _ = item3.FilePath.Should().Be("src/Gamma.cs");

        // All consumed → no more pending items
        QueueItem? exhausted = mgr.GetNext(experiment: 4);
        _ = exhausted.Should().BeNull("all items are in-progress, none pending");

        // Mark done with varied outcomes
        mgr.MarkDone(item1.Id, outcome: "improved", experiment: 1);
        mgr.MarkDone(item2.Id, outcome: "regressed", experiment: 2);
        mgr.MarkDone(item3.Id, outcome: "stale", experiment: 3);

        // Verify the JSON state
        string queueJsonPath = Path.Combine(metadataDir, "experiment-queue.json");
        _ = File.Exists(queueJsonPath).Should().BeTrue();

        string json = File.ReadAllText(queueJsonPath, Encoding.UTF8);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        JsonElement items = root.GetProperty("items");

        _ = items.GetArrayLength().Should().Be(3);

        JsonElement first = items[0];
        _ = first.GetProperty("filePath").GetString().Should().Be("src/Alpha.cs");
        _ = first.GetProperty("status").GetString().Should().Be("done");
        _ = first.GetProperty("outcome").GetString().Should().Be("improved");
        _ = first.GetProperty("triedByExperiment").GetInt32().Should().Be(1);

        JsonElement second = items[1];
        _ = second.GetProperty("filePath").GetString().Should().Be("src/Beta.cs");
        _ = second.GetProperty("status").GetString().Should().Be("done");
        _ = second.GetProperty("outcome").GetString().Should().Be("regressed");
        _ = second.GetProperty("triedByExperiment").GetInt32().Should().Be(2);

        JsonElement third = items[2];
        _ = third.GetProperty("filePath").GetString().Should().Be("src/Gamma.cs");
        _ = third.GetProperty("status").GetString().Should().Be("done");
        _ = third.GetProperty("outcome").GetString().Should().Be("stale");
        _ = third.GetProperty("triedByExperiment").GetInt32().Should().Be(3);

        // Markdown file should also be present and reflect done state
        string mdPath = Path.Combine(metadataDir, "experiment-queue.md");
        _ = File.Exists(mdPath).Should().BeTrue();
        string md = File.ReadAllText(mdPath, Encoding.UTF8);
        _ = md.Should().Contain("improved");
        _ = md.Should().Contain("regressed");
        _ = md.Should().Contain("stale");
    }

    // ── 4. Branch structure — stacked diffs ──────────────────────────────────

    /// <summary>
    /// Runs 3 successful experiments in stacked-diffs mode and verifies that branch
    /// names follow the hone/experiment-N pattern and that each PR's base branch
    /// correctly chains to the previous experiment's branch.
    /// </summary>
    [Fact]
    public async Task BranchStructure_StackedDiffs_CorrectBranchChain()
    {
        // Arrange: 3 experiments all succeed, stacked-diffs=true (default)
        TestHarness h = CreateHarness(configurePipeline: pipeline =>
        {
            _ = pipeline.RunAnalysisAsync(
                    Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AnalysisResult(
                    Success: true, Opportunities: MakeOpportunities(count: 5))));
            _ = pipeline.RunLoadTestAsync(
                    Arg.Any<LoadTestInput>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult(new LoadTestResult(
                    Success: true,
                    Metrics: MakeImprovedMetrics(
                        experiment: callInfo.Arg<LoadTestInput>().Experiment),
                    SummaryPath: null, Output: null)));
            _ = pipeline.CompareMetrics(
                    Arg.Any<MetricSet>(), Arg.Any<MetricSet>(), Arg.Any<MetricSet?>(),
                    Arg.Any<int>(), Arg.Any<HoneConfig>())
                .Returns(ImprovedComparison());
        });

        // Act
        LoopResult result = await h.Runner.RunAsync(h.MakeOptions(maxExperiments: 3));

        // Assert branch naming pattern
        _ = result.ExitReason.Should().Be("max_experiments");
        _ = result.SuccessCount.Should().Be(3);
        _ = result.BranchChain.Should().HaveCount(3);
        _ = result.BranchChain[0].Should().Be("hone/experiment-1");
        _ = result.BranchChain[1].Should().Be("hone/experiment-2");
        _ = result.BranchChain[2].Should().Be("hone/experiment-3");

        // Verify base branch chaining via captured CreatePrOptions calls:
        // exp-1 PR: base = "main" (default branch)
        // exp-2 PR: base = "hone/experiment-1"
        // exp-3 PR: base = "hone/experiment-2"
        List<CreatePrOptions> prCalls =
        [
            .. h.Pipeline
                .ReceivedCalls()
                .Where(c => string.Equals(
                    c.GetMethodInfo().Name,
                    nameof(ILoopPipeline.CreatePullRequestAsync),
                    StringComparison.Ordinal))
                .Select(c => (CreatePrOptions)c.GetArguments()[0]!),
        ];

        _ = prCalls.Should().HaveCount(3);
        _ = prCalls[0].BaseBranch.Should().Be("main",
            "first experiment PR should target the default branch");
        _ = prCalls[1].BaseBranch.Should().Be("hone/experiment-1",
            "second experiment PR should stack on the first experiment branch");
        _ = prCalls[2].BaseBranch.Should().Be("hone/experiment-2",
            "third experiment PR should stack on the second experiment branch");

        // Each head branch matches the corresponding chain entry
        _ = prCalls[0].HeadBranch.Should().Be("hone/experiment-1");
        _ = prCalls[1].HeadBranch.Should().Be("hone/experiment-2");
        _ = prCalls[2].HeadBranch.Should().Be("hone/experiment-3");
    }

    // ── 5. PR body snapshot ──────────────────────────────────────────────────

    /// <summary>
    /// Builds a PrBodyBuilder with known fixture data for an accepted experiment
    /// and verifies the markdown contains all expected sections.
    /// </summary>
    [Fact]
    public void PrBody_SnapshotTest_MatchesExpectedMarkdown()
    {
        var options = new PrBodyOptions
        {
            Type = PrBodyType.Accepted,
            Experiment = 5,
            Description = "Replace linear search with dictionary lookup in hot path",
            FilePath = "src/Api/Services/LookupService.cs",
            ImprovementPct = "18.4",
            StackNote = "> \U0001f4da This PR is part of a stacked experiment series.\n",
            MetricsSection = """
                ### Metrics Comparison
                | Metric | Baseline | Experiment |
                |--------|----------|------------|
                | p95    | 150ms    | 120ms      |
                | RPS    | 500      | 590        |
                | Errors | 0.00%    | 0.00%      |

                """,
            RcaSection = "\n### Root Cause Analysis\nLinear scan through large list causes O(n) overhead per request.\n",
        };

        string body = PrBodyBuilder.Build(options);

        // Heading must identify experiment number and NOT be marked rejected
        _ = body.Should().StartWith("## Hone Experiment 5");
        _ = body.Should().NotContain("[REJECTED]");

        // Description
        _ = body.Should().Contain("Replace linear search with dictionary lookup");

        // File path
        _ = body.Should().Contain("`src/Api/Services/LookupService.cs`");

        // Metrics table
        _ = body.Should().Contain("### Metrics Comparison");
        _ = body.Should().Contain("| p95    | 150ms    | 120ms      |");
        _ = body.Should().Contain("| RPS    | 500      | 590        |");

        // Decision / improvement
        _ = body.Should().Contain("**vs baseline improvement:** 18.4%");

        // RCA section
        _ = body.Should().Contain("Root Cause Analysis");
        _ = body.Should().Contain("Linear scan through large list");

        // Stack note
        _ = body.Should().Contain("stacked experiment series");

        // Footer always present
        _ = body.Should().Contain("*Auto-generated by the Hone agentic optimization harness.*");

        // Ordering: heading → stack note → description → metrics → improvement → footer
        int headingIdx = body.IndexOf("## Hone Experiment 5", StringComparison.Ordinal);
        int stackIdx = body.IndexOf("stacked experiment series", StringComparison.Ordinal);
        int descIdx = body.IndexOf("Replace linear search", StringComparison.Ordinal);
        int metricsIdx = body.IndexOf("### Metrics Comparison", StringComparison.Ordinal);
        int improvementIdx = body.IndexOf("18.4%", StringComparison.Ordinal);
        int footerIdx = body.IndexOf("Auto-generated", StringComparison.Ordinal);

        _ = headingIdx.Should().BeLessThan(stackIdx, "heading precedes stack note");
        _ = stackIdx.Should().BeLessThan(descIdx, "stack note precedes description");
        _ = descIdx.Should().BeLessThan(metricsIdx, "description precedes metrics");
        _ = metricsIdx.Should().BeLessThan(improvementIdx, "metrics precede improvement line");
        _ = improvementIdx.Should().BeLessThan(footerIdx, "improvement precedes footer");
    }

    // ── 6. RCA snapshot ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds an RCA document with known fixture data and verifies it contains
    /// all expected sections: header, performance issue table, impact estimate,
    /// root cause, and proposed fix.
    /// </summary>
    [Fact]
    public void RcaExport_SnapshotTest_MatchesExpectedMarkdown()
    {
        var fixedTimestamp = new DateTimeOffset(2024, 6, 15, 14, 30, 0, TimeSpan.Zero);

        var currentMetrics = new MetricSet(
            Timestamp: "2024-06-15T14:00:00Z",
            Experiment: 4,
            Run: 1,
            HttpReqDuration: new HttpReqDurationMetrics(
                Avg: 85, P50: 78, P90: 110, P95: 125, P99: 155, Max: 210),
            HttpReqs: new HttpReqCountMetrics(Count: 16500, Rate: 550),
            HttpReqFailed: new HttpReqFailedMetrics(Count: 0, Rate: 0),
            SummaryPath: null);

        var baselineMetrics = new MetricSet(
            Timestamp: "2024-06-15T12:00:00Z",
            Experiment: 0,
            Run: 1,
            HttpReqDuration: new HttpReqDurationMetrics(
                Avg: 100, P50: 90, P90: 140, P95: 150, P99: 185, Max: 255),
            HttpReqs: new HttpReqCountMetrics(Count: 15000, Rate: 500),
            HttpReqFailed: new HttpReqFailedMetrics(Count: 0, Rate: 0),
            SummaryPath: null);

        var comparison = new ComparisonResult(
            Accepted: true,
            Outcome: ExperimentOutcome.Improved,
            ImprovementPct: 16.7,
            RegressionPct: 0,
            Details:
            [
                new MetricComparison(
                    MetricName: "P95Latency",
                    Current: 125, Previous: 150, Baseline: 150,
                    DeltaPct: -0.167, AbsoluteDelta: 25,
                    Improved: true, Regressed: false),
                new MetricComparison(
                    MetricName: "RPS",
                    Current: 550, Previous: 500, Baseline: 500,
                    DeltaPct: 0.10, AbsoluteDelta: 50,
                    Improved: true, Regressed: false),
                new MetricComparison(
                    MetricName: "ErrorRate",
                    Current: 0, Previous: 0, Baseline: 0,
                    DeltaPct: 0, AbsoluteDelta: 0,
                    Improved: false, Regressed: false),
            ]);

        var impact = new ImpactEstimate(
            TrafficPct: 45.0,
            LatencyReductionMs: 25.0,
            OverallP95ImprovementPct: 7.5,
            Confidence: "high",
            Reasoning: "Highly trafficked endpoint; reduction is statistically significant.");

        var options = new RcaOptions
        {
            FilePath = "src/Api/Services/LookupService.cs",
            Explanation = "Dictionary lookup replaces O(n) linear scan in the search hot path.",
            ChangeScope = OpportunityScope.Narrow,
            ScopeReasoning = "Change is confined to a single method in one service class.",
            CodeBlock = "// optimized lookup\nreturn _cache.TryGetValue(key, out var val) ? val : null;",
            CurrentMetrics = currentMetrics,
            BaselineMetrics = baselineMetrics,
            ComparisonResult = comparison,
            ImpactEstimate = impact,
            CounterMetrics = null,
            BaselineCounterMetrics = null,
            Experiment = 4,
            GeneratedAtUtc = fixedTimestamp,
        };

        string md = RcaBuilder.Build(options);

        // Header
        _ = md.Should().Contain("# Root Cause Analysis \u2014 Experiment 4");
        _ = md.Should().Contain("> Generated: 2024-06-15 14:30:00 UTC");

        // Performance Issue section
        _ = md.Should().Contain("## Performance Issue");
        _ = md.Should().Contain("| p95 Latency |");
        _ = md.Should().Contain("| Requests/sec |");
        _ = md.Should().Contain("| Error Rate |");
        _ = md.Should().Contain("125ms"); // current p95
        _ = md.Should().Contain("150ms"); // baseline p95
        _ = md.Should().Contain("Overall improvement vs baseline: **16.7%** (p95 latency).");

        // Impact Estimate
        _ = md.Should().Contain("## Impact Estimate");
        _ = md.Should().Contain("| Traffic share | 45.0% |");
        _ = md.Should().Contain("| Confidence | high |");
        _ = md.Should().Contain("> Highly trafficked endpoint");

        // Root Cause / Rationale
        _ = md.Should().Contain("## Root Cause / Rationale");
        _ = md.Should().Contain("`src/Api/Services/LookupService.cs`");
        _ = md.Should().Contain("**Scope:** `narrow`");
        _ = md.Should().Contain("Dictionary lookup replaces O(n) linear scan");

        // Proposed Fix with code block
        _ = md.Should().Contain("## Proposed Fix");
        _ = md.Should().Contain("optimized lookup");

        // Efficiency sections should be absent (no counter metrics provided)
        _ = md.Should().NotContain("## Efficiency Metrics");
        _ = md.Should().NotContain("**Efficiency:**");

        // Section ordering
        int perfIdx = md.IndexOf("## Performance Issue", StringComparison.Ordinal);
        int impactIdx = md.IndexOf("## Impact Estimate", StringComparison.Ordinal);
        int rootCauseIdx = md.IndexOf("## Root Cause / Rationale", StringComparison.Ordinal);
        int fixIdx = md.IndexOf("## Proposed Fix", StringComparison.Ordinal);

        _ = perfIdx.Should().BeLessThan(impactIdx, "performance precedes impact");
        _ = impactIdx.Should().BeLessThan(rootCauseIdx, "impact precedes root cause");
        _ = rootCauseIdx.Should().BeLessThan(fixIdx, "root cause precedes proposed fix");
    }

    // ── 7. Full loop dry-run lifecycle ───────────────────────────────────────

    /// <summary>
    /// Simulates a complete dry-run loop using the integration test harness.
    /// Verifies the experiment lifecycle events were emitted (PhaseStarted, ExperimentOutcomeEvent)
    /// and the loop exits with an expected reason.
    /// </summary>
    [Fact]
    public async Task FullLoop_DryRun_CompletesWithCorrectLifecycle()
    {
        // Arrange: collect emitted events via a capturing sink
        var emittedEvents = new List<HoneEvent>();
        TestHarness h = CreateHarness(configurePipeline: pipeline =>
        {
            _ = pipeline.RunAnalysisAsync(
                    Arg.Any<AnalysisInput>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AnalysisResult(
                    Success: true, Opportunities: MakeOpportunities(count: 3))));
            _ = pipeline.CompareMetrics(
                    Arg.Any<MetricSet>(), Arg.Any<MetricSet>(), Arg.Any<MetricSet?>(),
                    Arg.Any<int>(), Arg.Any<HoneConfig>())
                .Returns(ImprovedComparison());
        });

        // Capture events through the event sink mock
        h.EventSink
            .When(s => s.Emit(Arg.Any<HoneEvent>()))
            .Do(call => emittedEvents.Add(call.Arg<HoneEvent>()));

        // Act: run 2 experiments in dry-run mode
        LoopResult result = await h.Runner.RunAsync(
            h.MakeOptions(dryRun: true, maxExperiments: 2));

        // Assert loop outcome
        _ = result.ExperimentsRun.Should().Be(2);
        _ = result.SuccessCount.Should().Be(2);
        _ = result.ExitReason.Should().BeOneOf("max_experiments", "no_improvement",
            "because dry-run loops exit on max_experiments or when no improvement is found");

        // Dry-run must not have called the real load test
        _ = await h.Pipeline.DidNotReceive().RunLoadTestAsync(
            Arg.Any<LoadTestInput>(), Arg.Any<CancellationToken>());

        // Events were emitted during the run
        _ = emittedEvents.Should().NotBeEmpty("the harness emits status events throughout the loop");

        // At least one ExperimentOutcomeEvent should have been emitted (one per experiment)
        IEnumerable<ExperimentOutcomeEvent> outcomeEvents =
            emittedEvents.OfType<ExperimentOutcomeEvent>();
        _ = outcomeEvents.Should().HaveCountGreaterThanOrEqualTo(2,
            "one outcome event per completed experiment");

        // Each outcome event indicates improvement (matching the mocked comparison)
        _ = outcomeEvents.Should().OnlyContain(e =>
            string.Equals(e.Outcome, "improved", StringComparison.OrdinalIgnoreCase) ||
            e.Details.Accepted,
            "all experiments were accepted in the mock");

        // PhaseStarted events should have been emitted for key pipeline phases
        IEnumerable<PhaseStarted> phaseStarted = emittedEvents.OfType<PhaseStarted>();
        _ = phaseStarted.Should().NotBeEmpty("phases are announced via PhaseStarted events");
    }
}
