using FluentAssertions;

using Hone.Diagnostics.Collectors;

using Xunit;

namespace Hone.Diagnostics.Tests.Collectors;

public sealed class FoldedStackParserTests
{
    private const string SampleCsv = """
        "Name","Exc","Exc%","Inc","Inc%","Fold","First","Last"
        "ROOT",0,"0.0%",100,"100.0%",0,0,0
        "System.Threading.ThreadPoolWorkQueue.Dispatch",50,"50.0%",80,"80.0%",1,0,0
        "MyApp!MyClass.DoWork",30,"30.0%",30,"30.0%",0,0,0
        "k6!main.run",10,"10.0%",10,"10.0%",0,0,0
        "conhost!ConsoleAllocate",5,"5.0%",5,"5.0%",0,0,0
        "MyApp!MyClass.ProcessRequest",8,"8.0%",8,"8.0%",0,0,0
        "ntdll!RtlUserThreadStart",3,"3.0%",3,"3.0%",0,0,0
        """;

    [Fact]
    public void ParsesCsvToFoldedFormat()
    {
        IReadOnlyList<string> result = FoldedStackParser.Parse(SampleCsv, filterExcludedModules: false, maxStacks: 100);

        // ROOT is excluded, so 6 entries
        _ = result.Should().HaveCount(6);

        // Each line should be "Name count"
        _ = result[0].Should().EndWith(" 50");
        _ = result[0].Should().Contain("ThreadPoolWorkQueue");
    }

    [Fact]
    public void FiltersExcludedModules()
    {
        IReadOnlyList<string> result = FoldedStackParser.Parse(SampleCsv, filterExcludedModules: true, maxStacks: 100);

        // k6 and conhost should be filtered out
        _ = result.Should().NotContain(line => line.Contains("k6!", StringComparison.Ordinal));
        _ = result.Should().NotContain(line => line.Contains("conhost!", StringComparison.Ordinal));

        // Other entries should remain (ThreadPoolWorkQueue doesn't have a module prefix to filter)
        _ = result.Should().Contain(line => line.Contains("MyApp!MyClass.DoWork", StringComparison.Ordinal));
        _ = result.Should().Contain(line => line.Contains("ntdll!RtlUserThreadStart", StringComparison.Ordinal));
    }

    [Fact]
    public void SortsByCountDescending()
    {
        IReadOnlyList<string> result = FoldedStackParser.Parse(SampleCsv, filterExcludedModules: false, maxStacks: 100);

        // Extract counts from "Name count" format
        int[] counts = [.. result.Select(line =>
        {
            string[] parts = line.Split(' ');
            return int.Parse(parts[^1], System.Globalization.CultureInfo.InvariantCulture);
        }),];

        // Counts should be in descending order
        for (int i = 0; i < counts.Length - 1; i++)
        {
            _ = counts[i].Should().BeGreaterThanOrEqualTo(counts[i + 1],
                $"count at index {i} ({counts[i]}) should be >= count at index {i + 1} ({counts[i + 1]})");
        }

        _ = counts[0].Should().Be(50, "highest count should be first");
    }

    [Fact]
    public void LimitsToMaxStacks()
    {
        IReadOnlyList<string> result = FoldedStackParser.Parse(SampleCsv, filterExcludedModules: false, maxStacks: 3);

        _ = result.Should().HaveCount(3);

        // Should be the top 3 by count (50, 30, 10)
        _ = result[0].Should().EndWith(" 50");
        _ = result[1].Should().EndWith(" 30");
    }

    [Fact]
    public void EmptyCsvReturnsEmpty()
    {
        IReadOnlyList<string> result = FoldedStackParser.Parse("", filterExcludedModules: false, maxStacks: 100);

        _ = result.Should().BeEmpty();
    }

    [Fact]
    public void HeaderOnlyReturnsEmpty()
    {
        string csv = "\"Name\",\"Exc\",\"Exc%\",\"Inc\",\"Inc%\",\"Fold\",\"First\",\"Last\"";
        IReadOnlyList<string> result = FoldedStackParser.Parse(csv, filterExcludedModules: false, maxStacks: 100);

        _ = result.Should().BeEmpty();
    }

    [Fact]
    public void HandlesCommasInCounts()
    {
        string csv = """
            "Name","Exc","Exc%","Inc","Inc%","Fold","First","Last"
            "MyApp!BigMethod","1,234","50.0%","1,234","100.0%",0,0,0
            """;

        IReadOnlyList<string> result = FoldedStackParser.Parse(csv, filterExcludedModules: false, maxStacks: 100);

        _ = result.Should().ContainSingle().Which.Should().Be("MyApp!BigMethod 1234");
    }
}
