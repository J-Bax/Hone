using System.Text.RegularExpressions;
using FluentAssertions;
using Hone.Reporting.Dashboard;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Reporting.Tests.Dashboard;

public sealed partial class DashboardExporterTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [GeneratedRegex(@"(?:src|href)\s*=\s*[""']https?://[^""']+[""']", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ExternalRefPattern();

    /// <summary>
    /// Creates a minimal but realistic <see cref="DashboardData"/> with a baseline
    /// and two experiments so charts and tables have enough data to render.
    /// </summary>
    private static DashboardData CreateSampleData(
        double minImprove = 5.0,
        double maxRegress = 3.0) =>
        new()
        {
            DataJson = """
                [
                  {"experiment":0,"label":"Baseline","p50":12.5,"p90":25.0,"p95":30.0,"avg":15.0,"max":80.0,"rps":1500.0,"reqCount":45000,"errRate":0.1},
                  {"experiment":1,"label":"Experiment 1","p50":11.0,"p90":22.0,"p95":26.5,"avg":13.5,"max":70.0,"rps":1620.0,"reqCount":48600,"errRate":0.08},
                  {"experiment":2,"label":"Experiment 2","p50":10.2,"p90":20.5,"p95":24.1,"avg":12.8,"max":65.0,"rps":1710.0,"reqCount":51300,"errRate":0.05}
                ]
                """,
            CounterJson = """
                [
                  {"experiment":1,"cpuAvg":45.2,"cpuMax":78.1,"heapMBAvg":120,"heapMBMax":180,"gen0":50,"gen1":10,"gen2":2,"workingSetMB":350,"threadPoolMax":25,"exceptions":0},
                  {"experiment":2,"cpuAvg":42.1,"cpuMax":72.5,"heapMBAvg":115,"heapMBMax":170,"gen0":48,"gen1":9,"gen2":1,"workingSetMB":340,"threadPoolMax":23,"exceptions":0}
                ]
                """,
            TimeSeriesJson = "{}",
            RunMetadataJson = "null",
            ScenarioJson = "{}",
            CounterChartJson = """
                [
                  {"experiment":1,"label":"Experiment 1","cpuAvg":45.2,"cpuMax":78.1,"workingSetMB":350.0,"heapMBMax":180.0},
                  {"experiment":2,"label":"Experiment 2","cpuAvg":42.1,"cpuMax":72.5,"workingSetMB":340.0,"heapMBMax":170.0}
                ]
                """,
            MinImprovePct = minImprove,
            MaxRegressPct = maxRegress,
            GeneratedAtUtc = new DateTimeOffset(2025, 1, 15, 10, 30, 0, TimeSpan.Zero),
        };

    [Fact]
    public void ExportDashboard_GeneratesValidHtml()
    {
        string html = DashboardExporter.Build(CreateSampleData());

        _ = html.Should().StartWith("<!DOCTYPE html>");
        _ = html.Should().Contain("<html lang=\"en\">");
        _ = html.Should().Contain("</html>");
        _ = html.Should().Contain("<head>");
        _ = html.Should().Contain("</head>");
        _ = html.Should().Contain("<body>");
        _ = html.Should().Contain("</body>");
    }

    [Fact]
    public void ExportDashboard_IncludesChartJs()
    {
        string html = DashboardExporter.Build(CreateSampleData());

        _ = html.Should().Contain("chart.js");
    }

    [Fact]
    public void ExportDashboard_LatencyTrend_AllExperiments()
    {
        string html = DashboardExporter.Build(CreateSampleData());

        // All three experiment p95 values should appear in the injected data
        _ = html.Should().Contain("30.0", "baseline p95 should be present");
        _ = html.Should().Contain("26.5", "experiment 1 p95 should be present");
        _ = html.Should().Contain("24.1", "experiment 2 p95 should be present");

        // Labels too
        _ = html.Should().Contain("Baseline");
        _ = html.Should().Contain("Experiment 1");
        _ = html.Should().Contain("Experiment 2");
    }

    [Fact]
    public void ExportDashboard_SelfContained_NoExternalRefs()
    {
        string html = DashboardExporter.Build(CreateSampleData());

        // The only acceptable external reference is the Chart.js CDN.
        MatchCollection externalRefs = ExternalRefPattern().Matches(html);

        foreach (Match match in externalRefs)
        {
            _ = match.Value.Should().Contain(
                "cdn.jsdelivr.net/npm/chart.js",
                $"unexpected external reference: {match.Value}");
        }
    }

    [Fact]
    public void ExportDashboard_InjectsTimestamp()
    {
        string html = DashboardExporter.Build(CreateSampleData());

        _ = html.Should().Contain("2025-01-15 10:30:00");
    }

    [Fact]
    public void ExportDashboard_InjectsThresholds()
    {
        string html = DashboardExporter.Build(CreateSampleData(minImprove: 7.5, maxRegress: 2.5));

        _ = html.Should().Contain("minImprovePct: 7.5");
        _ = html.Should().Contain("maxRegressPct: 2.5");
        _ = html.Should().Contain("Min improve: 7.5%");
        _ = html.Should().Contain("Max regress: 2.5%");
    }

    [Fact]
    public void ExportDashboard_NoPlaceholdersRemain()
    {
        string html = DashboardExporter.Build(CreateSampleData());

        _ = html.Should().NotContain("__DATA_JSON__");
        _ = html.Should().NotContain("__COUNTER_JSON__");
        _ = html.Should().NotContain("__TIMESERIES_JSON__");
        _ = html.Should().NotContain("__RUN_METADATA_JSON__");
        _ = html.Should().NotContain("__SCENARIO_JSON__");
        _ = html.Should().NotContain("__GENERATED_AT__");
        _ = html.Should().NotContain("__MIN_IMPROVE__");
        _ = html.Should().NotContain("__MAX_REGRESS__");
        _ = html.Should().NotContain("__COUNTER_CHART_JSON__");
    }

    [Fact]
    public void ExportDashboard_NullData_Throws()
    {
        Action act = () => DashboardExporter.Build(null!);

        _ = act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ExportDashboard_DefaultTimestamp_UsesUtcNow()
    {
        DashboardData data = CreateSampleData() with { GeneratedAtUtc = null };
        DateTimeOffset before = DateTimeOffset.UtcNow;

        string html = DashboardExporter.Build(data);

        // The generated timestamp should contain today's date
        _ = html.Should().Contain(before.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ExportDashboard_PreservesDarkThemeCss()
    {
        string html = DashboardExporter.Build(CreateSampleData());

        _ = html.Should().Contain("--bg: #0d1117");
        _ = html.Should().Contain("--surface: #161b22");
        _ = html.Should().Contain("--green: #3fb950");
        _ = html.Should().Contain("--red: #f85149");
    }

    [Fact]
    public void ExportDashboard_ContainsAllChartCanvases()
    {
        string html = DashboardExporter.Build(CreateSampleData());

        // Main charts
        _ = html.Should().Contain("id=\"latencyChart\"");
        _ = html.Should().Contain("id=\"throughputChart\"");
        _ = html.Should().Contain("id=\"distributionChart\"");
        _ = html.Should().Contain("id=\"comparisonChart\"");

        // Efficiency charts
        _ = html.Should().Contain("id=\"cpuTrendChart\"");
        _ = html.Should().Contain("id=\"memoryTrendChart\"");

        // Runtime diagnostics charts
        _ = html.Should().Contain("id=\"cpuChart\"");
        _ = html.Should().Contain("id=\"gcRateChart\"");
        _ = html.Should().Contain("id=\"allocChart\"");
        _ = html.Should().Contain("id=\"gcPauseChart\"");
        _ = html.Should().Contain("id=\"heapChart\"");
        _ = html.Should().Contain("id=\"lockChart\"");
        _ = html.Should().Contain("id=\"threadChart\"");
        _ = html.Should().Contain("id=\"memChart\"");
        _ = html.Should().Contain("id=\"reqExChart\"");
    }

    [Fact]
    public void ExportDashboard_ContainsAllJsFunctions()
    {
        string html = DashboardExporter.Build(CreateSampleData());

        _ = html.Should().Contain("function pctChange(");
        _ = html.Should().Contain("function renderCards(");
        _ = html.Should().Contain("function renderLatencyChart(");
        _ = html.Should().Contain("function renderThroughputChart(");
        _ = html.Should().Contain("function renderDistributionChart(");
        _ = html.Should().Contain("function renderComparisonChart(");
        _ = html.Should().Contain("function renderTable(");
        _ = html.Should().Contain("function renderScenarios(");
        _ = html.Should().Contain("function renderCpuTrendChart(");
        _ = html.Should().Contain("function renderMemoryTrendChart(");
        _ = html.Should().Contain("function renderIterSelector(");
        _ = html.Should().Contain("function selectExperiment(");
        _ = html.Should().Contain("function renderRuntimeCharts(");
    }
}
