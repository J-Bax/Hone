using FluentAssertions;
using Hone.Core.Utilities;
using Xunit;

namespace Hone.Core.Tests.Utilities;

public sealed class JsonUtilsTests
{
    // ── SanitizeNaN ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("{\"value\": NaN}", "{\"value\": null}")]
    [InlineData("{\"value\": Infinity}", "{\"value\": null}")]
    [InlineData("{\"v\": -Infinity}", "{\"v\": null}")]
    [InlineData("{\"count\": 42, \"name\": \"test\"}", "{\"count\": 42, \"name\": \"test\"}")]
    [InlineData("{\"msg\": \"NaN value\"}", "{\"msg\": \"NaN value\"}")]
    public void SanitizeNaN_ReturnsExpected(string input, string expected)
    {
        _ = JsonUtils.SanitizeNaN(input).Should().Be(expected);
    }

    // ── ExtractJsonBlock ────────────────────────────────────────────────

    [Fact]
    public void ExtractJsonBlock_FromMarkdownFences()
    {
        string input = "Here is the result:\n```json\n{\"key\": \"value\"}\n```\nDone.";
        string result = JsonUtils.ExtractJsonBlock(input);
        _ = result.Should().Be("{\"key\": \"value\"}");
    }

    [Fact]
    public void ExtractJsonBlock_NoFences_ReturnsOriginal()
    {
        string json = "{\"key\": \"value\"}";
        string result = JsonUtils.ExtractJsonBlock(json);
        _ = result.Should().Be(json);
    }

    [Fact]
    public void ExtractJsonBlock_GenericFences_FallsBack()
    {
        string input = "Result:\n```\n{\"a\": 1}\n```";
        string result = JsonUtils.ExtractJsonBlock(input);
        _ = result.Should().Be("{\"a\": 1}");
    }

    [Fact]
    public void ExtractJsonBlock_PrefixedChatterWithBraceNoise_ReturnsLargestValidJsonObject()
    {
        string input =
            """
            Let me check one more thing.
            Example remediation: Build: { Type: BuiltIn, Name: dotnet-build }
            Final answer:
            {"target":{"name":"SampleApi"},"compatibility":{"overall":"compatible","score":92}}
            """;

        string result = JsonUtils.ExtractJsonBlock(input);

        _ = result.Should().Be("{\"target\":{\"name\":\"SampleApi\"},\"compatibility\":{\"overall\":\"compatible\",\"score\":92}}");
    }

    [Fact]
    public void ExtractJsonBlock_NoJson_ReturnsOriginal()
    {
        string plain = "not json at all";
        string result = JsonUtils.ExtractJsonBlock(plain);
        _ = result.Should().Be(plain);
    }

    // ── ExtractCodeBlock ────────────────────────────────────────────────

    [Fact]
    public void ExtractCodeBlock_FromCSharpFences()
    {
        string input = "```csharp\nConsole.WriteLine(\"hi\");\n```";
        string result = JsonUtils.ExtractCodeBlock(input);
        _ = result.Should().Be("Console.WriteLine(\"hi\");");
    }

    [Fact]
    public void ExtractCodeBlock_GenericFences()
    {
        string input = "```\nsome code\n```";
        string result = JsonUtils.ExtractCodeBlock(input);
        _ = result.Should().Be("some code");
    }

    [Fact]
    public void ExtractCodeBlock_NoFences_ReturnsOriginal()
    {
        string plain = "just plain text";
        string result = JsonUtils.ExtractCodeBlock(plain);
        _ = result.Should().Be(plain);
    }
}
