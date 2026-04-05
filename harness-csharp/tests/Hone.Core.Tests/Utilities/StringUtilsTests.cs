using FluentAssertions;
using Hone.Core.Utilities;
using Xunit;

namespace Hone.Core.Tests.Utilities;

public sealed class StringUtilsTests
{
    [Theory]
    [InlineData("hello", 10, "hello")]
    [InlineData("hello world foo", 12, "hello world\u2026")]
    [InlineData("abcdefghij", 5, "abcde\u2026")]
    [InlineData(null, 10, null)]
    [InlineData("", 10, "")]
    [InlineData("hello", 5, "hello")]
    [InlineData("abcdef", 5, "abcde\u2026")]
    public void Truncate_ReturnsExpected(string? input, int max, string? expected)
    {
        _ = StringUtils.Truncate(input, max).Should().Be(expected);
    }
}
