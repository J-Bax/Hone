using System.Globalization;

using FluentAssertions;
using Hone.Core.Config;
using Hone.Core.Models;
using Hone.Reporting.Console;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Reporting.Tests.Console;

public sealed class ResultsRendererTests(ITestOutputHelper output) : HoneTestBase(output)
{
    // ───── Factory Methods ─────

    private static MetricSet CreateMetricSet(
        double p95 = 120.5,
        double avg = 72.3,
        double rps = 1500.0,
        double errorRate = 0.001,
        int experiment = 0,
        int run = 1) =>
        new(
            Timestamp: "2025-01-15T10:00:00Z",
            Experiment: experiment,
            Run: run,
            HttpReqDuration: new HttpReqDurationMetrics(
                Avg: avg,
                P50: p95 * 0.7,
                P90: p95 * 0.9,
                P95: p95,
                P99: p95 * 1.2,
                Max: p95 * 1.5),
            HttpReqs: new HttpReqCountMetrics(Count: (long)(rps * 30), Rate: rps),
            HttpReqFailed: new HttpReqFailedMetrics(Count: 3, Rate: errorRate),
            SummaryPath: null);

    private static ExperimentRow CreateExperimentRow(
        int experiment = 1,
        double p95 = 105.8,
        double avg = 63.5,
        double rps = 1620.0,
        double errorRate = 0.0008,
        ConsoleCounterData? counters = null) =>
        new(
            Experiment: experiment,
            Metrics: CreateMetricSet(
                p95: p95,
                avg: avg,
                rps: rps,
                errorRate: errorRate,
                experiment: experiment),
            Counters: counters);

    private static TolerancesConfig CreateTolerances(
        double maxRegression = 0.10,
        double minImprovement = 0.0) =>
        new(
            MaxRegressionPct: maxRegression,
            MinImprovementPct: minImprovement);

    private static ResultsViewModel CreateViewModel(
        MetricSet? baseline = null,
        IReadOnlyList<ExperimentRow>? experiments = null,
        TolerancesConfig? tolerances = null,
        RunMetadata? metadata = null,
        ConsoleCounterData? baselineCounters = null,
        IReadOnlyList<ScenarioResult>? scenarios = null) =>
        new(
            Baseline: baseline ?? CreateMetricSet(),
            Experiments: experiments ?? [],
            Tolerances: tolerances ?? CreateTolerances(),
            Metadata: metadata,
            BaselineCounters: baselineCounters,
            Scenarios: scenarios);

    private static string RenderToString(ResultsViewModel model)
    {
        using var stringWriter = new StringWriter(CultureInfo.InvariantCulture);
        var writer = new PlainTextColorWriter(stringWriter);
        ResultsRenderer.Render(model, writer);
        return stringWriter.ToString();
    }

    // ───── Required Tests ─────

    [Fact]
    public void RenderResults_FormatsTable()
    {
        // Arrange — baseline + one experiment
        ExperimentRow[] experiments =
        [
            CreateExperimentRow(
                experiment: 1,
                p95: 105.8,
                avg: 63.5,
                rps: 1620.0,
                errorRate: 0.0008),
        ];

        ResultsViewModel model = CreateViewModel(experiments: experiments);

        // Act
        string result = RenderToString(model);
        Output.WriteLine(result);

        // Assert — banner
        _ = result.Should().Contain("HONE PERFORMANCE RESULTS");

        // Assert — header columns
        _ = result.Should().Contain("Experiment");
        _ = result.Should().Contain("p95 (ms)");
        _ = result.Should().Contain("Avg (ms)");
        _ = result.Should().Contain("RPS");
        _ = result.Should().Contain("Error %");
        _ = result.Should().Contain("p95 \u0394");
        _ = result.Should().Contain("CPU avg%");
        _ = result.Should().Contain("Mem MB");

        // Assert — baseline row
        _ = result.Should().Contain("Baseline");
        _ = result.Should().Contain("120.5");
        _ = result.Should().Contain("72.3");
        _ = result.Should().Contain("1500");
        _ = result.Should().Contain("0.1%");

        // Assert — experiment row
        _ = result.Should().Contain("Experiment 1");
        _ = result.Should().Contain("105.8");
        _ = result.Should().Contain("63.5");
        _ = result.Should().Contain("1620");
        _ = result.Should().Contain("0.08%");

        // Assert — delta (p95 improved: (105.8 - 120.5) / 120.5 * 100 = -12.2%)
        _ = result.Should().Contain("-12.2%");

        // Assert — tolerance info
        _ = result.Should().Contain("Min improvement: 0%");
        _ = result.Should().Contain("Max regression: 10%");

        // Assert — latency distribution
        _ = result.Should().Contain("Latency Distribution (Baseline):");
        _ = result.Should().Contain("\u2588");

        // Assert — status
        _ = result.Should().Contain("IMPROVED vs BASELINE");
    }

    [Fact]
    public void RenderResults_HighlightsImprovements()
    {
        // Arrange — experiment with lower p95 and higher RPS
        ExperimentRow[] experiments =
        [
            CreateExperimentRow(
                experiment: 1,
                p95: 100.0,
                rps: 1800.0,
                errorRate: 0.0005),
        ];

        ResultsViewModel model = CreateViewModel(experiments: experiments);

        // Act
        string result = RenderToString(model);
        Output.WriteLine(result);

        // Assert — delta shows negative (improved) percentage for p95
        // (100 - 120.5) / 120.5 * 100 = -17%
        _ = result.Should().Contain("-17%");

        // Assert — status confirms improvement
        _ = result.Should().Contain("IMPROVED vs BASELINE");

        // Assert — experiment latency distribution is shown
        _ = result.Should().Contain("Latency Distribution (Experiment 1):");

        // Assert — RPS delta shown in status line
        // (1800 - 1500) / 1500 * 100 = +20%
        _ = result.Should().Contain("RPS +20%");
    }

    [Fact]
    public void RenderResults_EmptyResults_ShowsMessage()
    {
        // Arrange — no experiments
        ResultsViewModel model = CreateViewModel(experiments: []);

        // Act
        string result = RenderToString(model);
        Output.WriteLine(result);

        // Assert
        _ = result.Should().Contain("No optimization experiments yet.");
        _ = result.Should().Contain("Baseline");
        _ = result.Should().NotContain("IMPROVED");
    }

    // ───── Machine Info ─────

    [Fact]
    public void RenderResults_WithMachineInfo_ShowsCpuRamOs()
    {
        // Arrange
        var machineInfo = new MachineInfo(
            CpuName: "Intel Core i9-13900K",
            CpuCores: 24,
            TotalRamGB: 64m,
            OsVersion: "Windows 11 Pro 23H2",
            DotnetVersion: "9.0.100");

        var metadata = new RunMetadata(
            TargetName: "sample-api",
            StartedAt: "2025-01-15T10:00:00Z",
            MachineInfo: machineInfo,
            Experiments: []);

        ResultsViewModel model = CreateViewModel(metadata: metadata);

        // Act
        string result = RenderToString(model);
        Output.WriteLine(result);

        // Assert
        _ = result.Should().Contain("Intel Core i9-13900K");
        _ = result.Should().Contain("24 cores");
        _ = result.Should().Contain("RAM: 64GB");
        _ = result.Should().Contain("Windows 11 Pro 23H2");
        _ = result.Should().Contain(".NET 9.0.100");
    }

    [Fact]
    public void RenderResults_WithoutMachineInfo_SkipsSection()
    {
        // Arrange — no metadata
        ResultsViewModel model = CreateViewModel(metadata: null);

        // Act
        string result = RenderToString(model);

        // Assert — should not contain machine info labels
        _ = result.Should().NotContain("CPU:");
        _ = result.Should().NotContain("OS:");
        _ = result.Should().NotContain("Runtime:");
    }

    // ───── Counter Data ─────

    [Fact]
    public void RenderResults_WithCounterData_ShowsCpuAndMemory()
    {
        // Arrange
        var baselineCounters = new ConsoleCounterData(CpuAvgPercent: 45.2, MemoryMB: 512.3);
        var expCounters = new ConsoleCounterData(CpuAvgPercent: 38.1, MemoryMB: 498.7);

        ExperimentRow[] experiments =
        [
            CreateExperimentRow(experiment: 1, counters: expCounters),
        ];

        ResultsViewModel model = CreateViewModel(
            experiments: experiments,
            baselineCounters: baselineCounters);

        // Act
        string result = RenderToString(model);
        Output.WriteLine(result);

        // Assert — baseline counters
        _ = result.Should().Contain("45.2");
        _ = result.Should().Contain("512.3");

        // Assert — experiment counters
        _ = result.Should().Contain("38.1");
        _ = result.Should().Contain("498.7");
    }

    [Fact]
    public void RenderResults_WithoutCounterData_ShowsDash()
    {
        // Arrange — no counter data
        ExperimentRow[] experiments =
        [
            CreateExperimentRow(experiment: 1, counters: null),
        ];

        ResultsViewModel model = CreateViewModel(
            experiments: experiments,
            baselineCounters: null);

        // Act
        string result = RenderToString(model);
        Output.WriteLine(result);

        // Assert — dash character for unavailable counters
        _ = result.Should().Contain("\u2014");
    }

    // ───── Scenario Breakdown ─────

    [Fact]
    public void RenderResults_WithScenarios_ShowsBreakdown()
    {
        // Arrange
        var scenarioBaseline = new ExperimentRow(
            Experiment: 0,
            Metrics: CreateMetricSet(p95: 80.0, rps: 2000.0, errorRate: 0.0));

        var scenarioExperiment = new ExperimentRow(
            Experiment: 1,
            Metrics: CreateMetricSet(p95: 70.0, rps: 2200.0, errorRate: 0.0, experiment: 1));

        ScenarioResult[] scenarios =
        [
            new ScenarioResult(
                ScenarioName: "health-check",
                Baseline: scenarioBaseline,
                Experiments: [scenarioExperiment]),
        ];

        ExperimentRow[] experiments =
        [
            CreateExperimentRow(experiment: 1),
        ];

        ResultsViewModel model = CreateViewModel(
            experiments: experiments,
            scenarios: scenarios);

        // Act
        string result = RenderToString(model);
        Output.WriteLine(result);

        // Assert
        _ = result.Should().Contain("Scenario Breakdown");
        _ = result.Should().Contain("health-check");
    }

    [Fact]
    public void RenderResults_WithoutScenarios_SkipsBreakdown()
    {
        // Arrange
        ResultsViewModel model = CreateViewModel(scenarios: null);

        // Act
        string result = RenderToString(model);

        // Assert
        _ = result.Should().NotContain("Scenario Breakdown");
    }

    // ───── Latency Distribution ─────

    [Fact]
    public void RenderResults_LatencyDistribution_ShowsBarsForPercentiles()
    {
        // Arrange
        ExperimentRow[] experiments =
        [
            CreateExperimentRow(experiment: 1, p95: 100.0),
        ];

        ResultsViewModel model = CreateViewModel(experiments: experiments);

        // Act
        string result = RenderToString(model);
        Output.WriteLine(result);

        // Assert — baseline distribution
        _ = result.Should().Contain("Latency Distribution (Baseline):");
        _ = result.Should().Contain("p50");
        _ = result.Should().Contain("p90");
        _ = result.Should().Contain("p95");
        _ = result.Should().Contain("Max");

        // Assert — experiment distribution
        _ = result.Should().Contain("Latency Distribution (Experiment 1):");
    }

    // ───── Status ─────

    [Fact]
    public void RenderResults_NoImprovement_ShowsNoNetImprovement()
    {
        // Arrange — experiment with worse metrics
        ExperimentRow[] experiments =
        [
            CreateExperimentRow(
                experiment: 1,
                p95: 130.0,
                rps: 1400.0,
                errorRate: 0.002),
        ];

        ResultsViewModel model = CreateViewModel(experiments: experiments);

        // Act
        string result = RenderToString(model);
        Output.WriteLine(result);

        // Assert
        _ = result.Should().Contain("NO NET IMPROVEMENT vs BASELINE");
    }

    [Fact]
    public void RenderResults_EfficiencySummary_ShowsCpuAndMemoryDelta()
    {
        // Arrange
        var baselineCounters = new ConsoleCounterData(CpuAvgPercent: 50.0, MemoryMB: 500.0);
        var expCounters = new ConsoleCounterData(CpuAvgPercent: 40.0, MemoryMB: 450.0);

        ExperimentRow[] experiments =
        [
            CreateExperimentRow(experiment: 1, counters: expCounters),
        ];

        ResultsViewModel model = CreateViewModel(
            experiments: experiments,
            baselineCounters: baselineCounters);

        // Act
        string result = RenderToString(model);
        Output.WriteLine(result);

        // Assert — efficiency summary in status section
        _ = result.Should().Contain("Efficiency:");
        _ = result.Should().Contain("CPU -20%");
        _ = result.Should().Contain("Memory -10%");
    }

    // ───── Null Validation ─────

    [Fact]
    public void Render_ThrowsOnNullModel()
    {
        using var stringWriter = new StringWriter();
        var writer = new PlainTextColorWriter(stringWriter);

        Action act = () => ResultsRenderer.Render(null!, writer);

        _ = act.Should().Throw<ArgumentNullException>()
            .WithParameterName("model");
    }

    [Fact]
    public void Render_ThrowsOnNullWriter()
    {
        ResultsViewModel model = CreateViewModel();

        Action act = () => ResultsRenderer.Render(model, null!);

        _ = act.Should().Throw<ArgumentNullException>()
            .WithParameterName("writer");
    }

    // ───── Test Double ─────

    private sealed class PlainTextColorWriter(TextWriter writer) : IConsoleColorWriter
    {
        public void Write(string text, ConsoleColor? color = null) => writer.Write(text);

        public void WriteLine(string text = "", ConsoleColor? color = null) => writer.WriteLine(text);
    }
}