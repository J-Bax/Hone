using FluentAssertions;

using Hone.Diagnostics.Collectors;

using Xunit;

namespace Hone.Diagnostics.Tests.Collectors;

public sealed class GcReportParserTests
{
    /// <summary>
    /// Sample HTML mimicking PerfView's GCStats output for a single process.
    /// </summary>
    private const string SampleHtml = """
        <HTML>
        <BODY>
        <HR/>
        <H3>GC Stats for Process dotnet (1234)</H3>
        <H4>GC Rollup By Generation</H4>
        <TABLE>
        <TR><TH>Gen</TH><TH>Count</TH><TH>MaxPause</TH><TH>MaxPeakMB</TH><TH>MaxAllocMBSec</TH><TH>TotalPause</TH><TH>TotalAllocMB</TH><TH>MeanSizeMB</TH><TH>MeanAllocMBSec</TH><TH>MeanPause</TH><TH>Induced</TH></TR>
        <TR><TD>0</TD><TD>42</TD><TD>5.123</TD><TD>100</TD><TD>200</TD><TD>50</TD><TD>1000</TD><TD>80</TD><TD>150</TD><TD>1.19</TD><TD>0</TD></TR>
        <TR><TD>1</TD><TD>10</TD><TD>8.456</TD><TD>100</TD><TD>200</TD><TD>30</TD><TD>500</TD><TD>90</TD><TD>180</TD><TD>3.0</TD><TD>0</TD></TR>
        <TR><TD>2</TD><TD>3</TD><TD>15.789</TD><TD>100</TD><TD>200</TD><TD>20</TD><TD>300</TD><TD>95</TD><TD>190</TD><TD>6.667</TD><TD>0</TD></TR>
        <TR><TD>ALL</TD><TD>55</TD><TD>15.789</TD><TD>100</TD><TD>200</TD><TD>100</TD><TD>1800</TD><TD>88</TD><TD>170</TD><TD>1.818</TD><TD>0</TD></TR>
        </TABLE>
        <UL>
        <LI>Total GC Pause: 100.456 msec</LI>
        <LI>% Time paused for Garbage Collection: 2.34%</LI>
        <LI>Max GC Heap Size: 256.78 MB</LI>
        <LI>Total Allocs : 4096.50 MB</LI>
        <LI>Alloc Rate: 123.45 MB/sec</LI>
        </UL>
        <HR/>
        </BODY>
        </HTML>
        """;

    [Fact]
    public void ParsesGenerationStats()
    {
        GcReport report = GcReportParser.Parse(SampleHtml, "dotnet");

        _ = report.GenerationStats.Gen0.Count.Should().Be(42);
        _ = report.GenerationStats.Gen0.MaxPauseMs.Should().Be(5.123);
        _ = report.GenerationStats.Gen0.AvgPauseMs.Should().Be(1.19);

        _ = report.GenerationStats.Gen1.Count.Should().Be(10);
        _ = report.GenerationStats.Gen1.MaxPauseMs.Should().Be(8.456);
        _ = report.GenerationStats.Gen1.AvgPauseMs.Should().Be(3.0);

        _ = report.GenerationStats.Gen2.Count.Should().Be(3);
        _ = report.GenerationStats.Gen2.MaxPauseMs.Should().Be(15.789);
        _ = report.GenerationStats.Gen2.AvgPauseMs.Should().Be(6.667);
    }

    [Fact]
    public void ParsesHeapStats()
    {
        GcReport report = GcReportParser.Parse(SampleHtml, "dotnet");

        _ = report.HeapStats.PeakSizeMB.Should().Be(256.78);
        _ = report.HeapStats.TotalAllocMB.Should().Be(4096.50);
    }

    [Fact]
    public void ParsesPauseStats()
    {
        GcReport report = GcReportParser.Parse(SampleHtml, "dotnet");

        _ = report.PauseStats.TotalPauseMs.Should().Be(100.456);
        _ = report.PauseStats.MaxPauseMs.Should().Be(15.789);
        _ = report.PauseStats.GcPauseRatio.Should().Be(2.34);
    }

    [Fact]
    public void ParsesAllocationStats()
    {
        GcReport report = GcReportParser.Parse(SampleHtml, "dotnet");

        _ = report.AllocationStats.AllocRateMBSec.Should().Be(123.45);
    }

    [Fact]
    public void EmptyHtmlReturnsDefaultReport()
    {
        GcReport report = GcReportParser.Parse("", "dotnet");

        _ = report.GenerationStats.Gen0.Count.Should().Be(0);
        _ = report.HeapStats.PeakSizeMB.Should().Be(0);
        _ = report.PauseStats.TotalPauseMs.Should().Be(0);
    }

    [Fact]
    public void FallsBackToFullHtmlWhenProcessNotFound()
    {
        // Use a process name that won't match the section heading
        GcReport report = GcReportParser.Parse(SampleHtml, "nonexistent-process");

        // Should still parse because it falls back to any GC Rollup section
        _ = report.GenerationStats.Gen0.Count.Should().Be(42);
    }

    [Fact]
    public void HandlesMultipleProcessSections()
    {
        // HTML with two process sections
        string html = """
            <HR/>
            <H3>GC Stats for Process other (999)</H3>
            <H4>GC Rollup By Generation</H4>
            <TABLE>
            <TR><TH>Gen</TH><TH>Count</TH><TH>MaxPause</TH><TH>MaxPeakMB</TH><TH>MaxAllocMBSec</TH><TH>TotalPause</TH><TH>TotalAllocMB</TH><TH>MeanSizeMB</TH><TH>MeanAllocMBSec</TH><TH>MeanPause</TH><TH>Induced</TH></TR>
            <TR><TD>0</TD><TD>5</TD><TD>1.0</TD><TD>50</TD><TD>100</TD><TD>10</TD><TD>200</TD><TD>40</TD><TD>80</TD><TD>2.0</TD><TD>0</TD></TR>
            </TABLE>
            <UL>
            <LI>Total GC Pause: 10.0 msec</LI>
            <LI>% Time paused for Garbage Collection: 0.5%</LI>
            <LI>Max GC Heap Size: 50.0 MB</LI>
            <LI>Total Allocs : 200.0 MB</LI>
            </UL>
            <HR/>
            <H3>GC Stats for Process myapp (1234)</H3>
            <H4>GC Rollup By Generation</H4>
            <TABLE>
            <TR><TH>Gen</TH><TH>Count</TH><TH>MaxPause</TH><TH>MaxPeakMB</TH><TH>MaxAllocMBSec</TH><TH>TotalPause</TH><TH>TotalAllocMB</TH><TH>MeanSizeMB</TH><TH>MeanAllocMBSec</TH><TH>MeanPause</TH><TH>Induced</TH></TR>
            <TR><TD>0</TD><TD>42</TD><TD>5.123</TD><TD>100</TD><TD>200</TD><TD>50</TD><TD>1000</TD><TD>80</TD><TD>150</TD><TD>1.19</TD><TD>0</TD></TR>
            </TABLE>
            <UL>
            <LI>Total GC Pause: 100.0 msec</LI>
            <LI>% Time paused for Garbage Collection: 2.0%</LI>
            <LI>Max GC Heap Size: 256.0 MB</LI>
            <LI>Total Allocs : 4096.0 MB</LI>
            </UL>
            <HR/>
            """;

        GcReport report = GcReportParser.Parse(html, "myapp");

        // Should pick the myapp section, not the other process
        _ = report.GenerationStats.Gen0.Count.Should().Be(42);
        _ = report.HeapStats.PeakSizeMB.Should().Be(256.0);
    }
}
