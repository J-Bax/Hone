using FluentAssertions;
using Hone.Core.Utilities;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Utilities;

public sealed class JsonUtilsTests(ITestOutputHelper output) : HoneTestBase(output)
{
    // ── SanitizeNaN ─────────────────────────────────────────────────────

    [Fact]
    public void SanitizeNaN_ReplacesNaN()
    {
        string result = JsonUtils.SanitizeNaN("{\"value\": NaN}");
        _ = result.Should().Be("{\"value\": null}");
    }

    [Fact]
    public void SanitizeNaN_ReplacesInfinity()
    {
        string result = JsonUtils.SanitizeNaN("{\"value\": Infinity}");
        _ = result.Should().Be("{\"value\": null}");
    }

    [Fact]
    public void SanitizeNaN_NegativeInfinity()
    {
        string result = JsonUtils.SanitizeNaN("{\"v\": -Infinity}");
        _ = result.Should().Be("{\"v\": null}");
    }

    [Fact]
    public void SanitizeNaN_NoNaN_ReturnsUnchanged()
    {
        string json = "{\"count\": 42, \"name\": \"test\"}";
        string result = JsonUtils.SanitizeNaN(json);
        _ = result.Should().Be(json);
    }

    [Fact]
    public void SanitizeNaN_NaNInsideString_NotReplaced()
    {
        string json = "{\"msg\": \"NaN value\"}";
        string result = JsonUtils.SanitizeNaN(json);
        _ = result.Should().Be(json);
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
