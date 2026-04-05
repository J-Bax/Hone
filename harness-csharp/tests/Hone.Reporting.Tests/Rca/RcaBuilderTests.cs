using FluentAssertions;
using Hone.Core.Models;
using Hone.Reporting.Rca;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Reporting.Tests.Rca;

public sealed class RcaBuilderTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(2025, 1, 15, 10, 30, 0, TimeSpan.Zero);

    private static MetricSet CreateMetricSet(
        double p95 = 120.5,
        double rps = 1500.0,
        double errorRate = 0.001,
        int experiment = 1,
        int run = 1) =>
        new(
            Timestamp: "2025-01-15T10:00:00Z",
            Experiment: experiment,
            Run: run,
            HttpReqDuration: new HttpReqDurationMetrics(
                Avg: p95 * 0.6,
                P50: p95 * 0.7,
                P90: p95 * 0.9,
                P95: p95,
                P99: p95 * 1.2,
                Max: p95 * 1.5),
            HttpReqs: new HttpReqCountMetrics(Count: (long)(rps * 30), Rate: rps),
            HttpReqFailed: new HttpReqFailedMetrics(Count: 3, Rate: errorRate),
            SummaryPath: null);

    private static ComparisonResult CreateComparisonResult(
        double improvementPct = 12.3,
        double p95DeltaPct = -12.3,
        double rpsDeltaPct = 8.5,
        double errorDeltaPct = -0.5) =>
        new(
            Accepted: true,
            Outcome: ExperimentOutcome.Improved,
            ImprovementPct: improvementPct,
            RegressionPct: 0,
            Details:
            [
                new MetricComparison(MetricName: "P95Latency", Current: 105.8, Previous: 120.5, Baseline: 120.5, DeltaPct: p95DeltaPct, AbsoluteDelta: 14.7, Improved: true, Regressed: false),
                new MetricComparison(MetricName: "RPS", Current: 1620.0, Previous: 1500.0, Baseline: 1500.0, DeltaPct: rpsDeltaPct, AbsoluteDelta: 120.0, Improved: true, Regressed: false),
                new MetricComparison(MetricName: "ErrorRate", Current: 0.0008, Previous: 0.001, Baseline: 0.001, DeltaPct: errorDeltaPct, AbsoluteDelta: 0.0002, Improved: true, Regressed: false),
            ]);

    private static ImpactEstimate CreateImpactEstimate() =>
        new(
            TrafficPct: 35.0,
            LatencyReductionMs: 14.7,
            OverallP95ImprovementPct: 4.3,
            Confidence: "medium",
            Reasoning: "Based on traffic analysis and observed p95 reduction.");

    private static CounterSnapshot CreateCounterSnapshot(
        double cpuAvg = 45.2,
        double cpuMax = 78.1,
        double memAvg = 512.3,
        double memMax = 768.9) =>
        new(CpuAvg: cpuAvg, CpuMax: cpuMax, WorkingSetMBAvg: memAvg, WorkingSetMBMax: memMax);

    private static RcaOptions CreateFullOptions() =>
        new()
        {
            FilePath = "src/Api/Handlers/OrderHandler.cs",
            Explanation = "Excessive allocations in the hot path cause GC pressure under load.",
            ChangeScope = OpportunityScope.Narrow,
            ScopeReasoning = "Change is limited to a single method in one file.",
            CodeBlock = "public void Handle() { /* optimized */ }",
            CurrentMetrics = CreateMetricSet(p95: 105.8, rps: 1620.0, errorRate: 0.0008),
            BaselineMetrics = CreateMetricSet(p95: 120.5, rps: 1500.0, errorRate: 0.001),
            ComparisonResult = CreateComparisonResult(),
            ImpactEstimate = CreateImpactEstimate(),
            CounterMetrics = CreateCounterSnapshot(cpuAvg: 42.0, cpuMax: 72.0, memAvg: 490.0, memMax: 710.0),
            BaselineCounterMetrics = CreateCounterSnapshot(cpuAvg: 45.2, cpuMax: 78.1, memAvg: 512.3, memMax: 768.9),
            Experiment = 3,
            GeneratedAtUtc = FixedTimestamp,
        };

    // ───── Required tests ─────

    [Fact]
    public void ExportRCA_ContainsAllSections()
    {
        RcaOptions options = CreateFullOptions();

        string md = RcaBuilder.Build(options);

        // Header
        _ = md.Should().Contain("# Root Cause Analysis \u2014 Experiment 3");
        _ = md.Should().Contain("> Generated: 2025-01-15 10:30:00 UTC");

        // Performance Issue section with metrics table
        _ = md.Should().Contain("## Performance Issue");
        _ = md.Should().Contain("| p95 Latency |");
        _ = md.Should().Contain("| Requests/sec |");
        _ = md.Should().Contain("| Error Rate |");
        _ = md.Should().Contain("Overall improvement vs baseline: **12.3%** (p95 latency).");

        // Impact Estimate
        _ = md.Should().Contain("## Impact Estimate");
        _ = md.Should().Contain("| Traffic share | 35.0% |");
        _ = md.Should().Contain("| Confidence | medium |");
        _ = md.Should().Contain("> Based on traffic analysis");

        // Efficiency summary
        _ = md.Should().Contain("**Efficiency:**");

        // Efficiency Metrics table
        _ = md.Should().Contain("## Efficiency Metrics");
        _ = md.Should().Contain("| CPU avg |");

        // Root Cause / Rationale
        _ = md.Should().Contain("## Root Cause / Rationale");
        _ = md.Should().Contain("**Target file:** `src/Api/Handlers/OrderHandler.cs`");
        _ = md.Should().Contain("**Scope:** `narrow`");
        _ = md.Should().Contain("Excessive allocations in the hot path");

        // Proposed Fix with code block
        _ = md.Should().Contain("## Proposed Fix");
        _ = md.Should().Contain("public void Handle() { /* optimized */ }");
    }

    [Fact]
    public void ExportRCA_MarkdownValid()
    {
        RcaOptions options = CreateFullOptions();

        string md = RcaBuilder.Build(options);

        // All headers use proper markdown syntax
        _ = md.Should().Contain("# Root Cause Analysis");
        _ = md.Should().Contain("## Performance Issue");
        _ = md.Should().Contain("## Impact Estimate");
        _ = md.Should().Contain("## Efficiency Metrics");
        _ = md.Should().Contain("## Root Cause / Rationale");
        _ = md.Should().Contain("## Proposed Fix");

        // Tables have proper separators
        _ = md.Should().Contain("|--------|---------|----------|-------|");
        _ = md.Should().Contain("|--------|----------|");
        _ = md.Should().Contain("|--------|---------|----------|");

        // Code block is properly fenced
        int firstFence = md.IndexOf("```", md.IndexOf("## Proposed Fix", StringComparison.Ordinal), StringComparison.Ordinal);
        int secondFence = md.IndexOf("```", firstFence + 3, StringComparison.Ordinal);
        _ = firstFence.Should().BeGreaterThanOrEqualTo(0, "opening code fence should exist");
        _ = secondFence.Should().BeGreaterThan(firstFence, "closing code fence should exist after opening");

        // Blockquote uses proper syntax
        _ = md.Should().Contain("> Generated:");
        _ = md.Should().Contain("> Based on traffic analysis");
    }

    [Fact]
    public void ExportRCA_IterativeFixer_AppendsSummary()
    {
        string iterationSection = """
            ### Iteration Summary

            | Attempt | Stage | Outcome |
            |---------|-------|---------|
            | 1 | build | fail |
            | 2 | build | pass |
            | 3 | test | pass |

            """;

        RcaOptions options = CreateFullOptions() with
        {
            IterationSummarySection = iterationSection,
        };

        string md = RcaBuilder.Build(options);

        _ = md.Should().Contain("### Iteration Summary");
        _ = md.Should().Contain("| 1 | build | fail |");
        _ = md.Should().Contain("| 3 | test | pass |");

        // Iteration summary appears between Root Cause and Proposed Fix
        int rootCauseIndex = md.IndexOf("## Root Cause / Rationale", StringComparison.Ordinal);
        int iterationIndex = md.IndexOf("### Iteration Summary", StringComparison.Ordinal);
        int proposedFixIndex = md.IndexOf("## Proposed Fix", StringComparison.Ordinal);

        _ = iterationIndex.Should().BeGreaterThan(rootCauseIndex);
        _ = proposedFixIndex.Should().BeGreaterThan(iterationIndex);
    }

    // ───── Conditional sections ─────

    [Fact]
    public void Build_NoImpactEstimate_SectionOmitted()
    {
        RcaOptions options = CreateFullOptions() with { ImpactEstimate = null };

        string md = RcaBuilder.Build(options);

        _ = md.Should().NotContain("## Impact Estimate");
        _ = md.Should().NotContain("Traffic share");
    }

    [Fact]
    public void Build_NoCounterMetrics_EfficiencySectionsOmitted()
    {
        RcaOptions options = CreateFullOptions() with
        {
            CounterMetrics = null,
            BaselineCounterMetrics = null,
        };

        string md = RcaBuilder.Build(options);

        _ = md.Should().NotContain("## Efficiency Metrics");
        _ = md.Should().NotContain("**Efficiency:**");
    }

    [Fact]
    public void Build_CounterMetricsWithoutBaseline_ShowsDashForBaseline()
    {
        RcaOptions options = CreateFullOptions() with
        {
            CounterMetrics = CreateCounterSnapshot(),
            BaselineCounterMetrics = null,
        };

        string md = RcaBuilder.Build(options);

        _ = md.Should().Contain("## Efficiency Metrics");
        _ = md.Should().NotContain("**Efficiency:**");
        _ = md.Should().Contain("\u2014");
    }

    [Fact]
    public void Build_NoComparisonResult_DeltasShowNA()
    {
        RcaOptions options = CreateFullOptions() with { ComparisonResult = null };

        string md = RcaBuilder.Build(options);

        // All delta cells should show N/A
        _ = md.Should().Contain("| p95 Latency | 105.8ms | 120.5ms | N/A |");
        _ = md.Should().Contain("| N/A |");
        _ = md.Should().NotContain("Overall improvement vs baseline");
    }

    [Fact]
    public void Build_NoCodeBlock_ShowsPlaceholderText()
    {
        RcaOptions options = CreateFullOptions() with { CodeBlock = null };

        string md = RcaBuilder.Build(options);

        _ = md.Should().Contain("## Proposed Fix");
        _ = md.Should().Contain("No code block provided");
        _ = md.Should().Contain("see the experiment branch");
        _ = md.Should().NotContain("```");
    }

    [Fact]
    public void Build_NoIterationSummary_SectionOmitted()
    {
        RcaOptions options = CreateFullOptions() with { IterationSummarySection = null };

        string md = RcaBuilder.Build(options);

        _ = md.Should().NotContain("Iteration Summary");
    }

    // ───── Formatting ─────

    [Fact]
    public void Build_DeltaFormatting_ShowsSignAndOneDecimal()
    {
        RcaOptions options = CreateFullOptions();

        string md = RcaBuilder.Build(options);

        // Negative delta (improvement for latency)
        _ = md.Should().Contain("-12.3%");
        // Positive delta (improvement for RPS)
        _ = md.Should().Contain("+8.5%");
    }

    [Fact]
    public void Build_ErrorRateMultipliedBy100()
    {
        RcaOptions options = CreateFullOptions();

        string md = RcaBuilder.Build(options);

        // Error rate 0.0008 * 100 = 0.08
        _ = md.Should().Contain("| Error Rate | 0.08% |");
        // Baseline error rate 0.001 * 100 = 0.10
        _ = md.Should().Contain("0.10%");
    }

    [Fact]
    public void Build_ArchitectureScope_FormatsCorrectly()
    {
        RcaOptions options = CreateFullOptions() with
        {
            ChangeScope = OpportunityScope.Architecture,
            ScopeReasoning = "Requires restructuring the dependency graph.",
        };

        string md = RcaBuilder.Build(options);

        _ = md.Should().Contain("**Scope:** `architecture` \u2014 Requires restructuring the dependency graph.");
    }

    [Fact]
    public void Build_ThrowsOnNullOptions()
    {
        Action act = () => RcaBuilder.Build(null!);

        _ = act.Should().Throw<ArgumentNullException>();
    }
}
