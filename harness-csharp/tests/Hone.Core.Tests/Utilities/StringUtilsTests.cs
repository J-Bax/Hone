using FluentAssertions;
using Hone.Core.Utilities;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Utilities;

public sealed class StringUtilsTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void Truncate_ShortString_ReturnsUnchanged()
    {
        string result = StringUtils.Truncate("hello", 10)!;
        _ = result.Should().Be("hello");
    }

    [Fact]
    public void Truncate_AtWordBoundary_AppendsEllipsis()
    {
        string result = StringUtils.Truncate("hello world foo", 12)!;
        _ = result.Should().Be("hello world\u2026");
    }

    [Fact]
    public void Truncate_NoSpaceInFirstHalf_TruncatesHard()
    {
        string result = StringUtils.Truncate("abcdefghij", 5)!;
        _ = result.Should().Be("abcde\u2026");
    }

    [Fact]
    public void Truncate_Null_ReturnsNull()
    {
        string? result = StringUtils.Truncate(text: null, maxLength: 10);
        _ = result.Should().BeNull();
    }

    [Fact]
    public void Truncate_Empty_ReturnsEmpty()
    {
        string? result = StringUtils.Truncate("", 10);
        _ = result.Should().BeEmpty();
    }

    [Fact]
    public void Truncate_ExactLength_ReturnsUnchanged()
    {
        string result = StringUtils.Truncate("hello", 5)!;
        _ = result.Should().Be("hello");
    }

    [Fact]
    public void Truncate_OneOverMax_Truncates()
    {
        // "abcdef" is 6 chars, max=5 → must truncate
        string result = StringUtils.Truncate("abcdef", 5)!;
        _ = result.Should().Be("abcde\u2026");
    }
}
