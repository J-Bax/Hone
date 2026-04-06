using System.Text.RegularExpressions;

namespace Hone.Core.Utilities;

/// <summary>
/// JSON pre-processing helpers for sanitising k6 output and extracting
/// code blocks from markdown-fenced LLM responses.
/// </summary>
public static partial class JsonUtils
{
    /// <summary>
    /// Replaces bare <c>NaN</c>, <c>Infinity</c>, and <c>-Infinity</c> values
    /// in JSON text with <c>null</c>.  k6 can produce these in metric summaries.
    /// </summary>
    /// <remarks>
    /// The regex targets values that follow a colon (i.e. JSON property values)
    /// so that occurrences inside quoted strings are left untouched.
    /// </remarks>
    public static string SanitizeNaN(string json)
    {
        return NaNPattern().Replace(json, ": null");
    }

    /// <summary>
    /// Extracts the first JSON block from markdown-fenced text
    /// (<c>```json … ```</c>).  Falls back to generic fences, then
    /// returns the original text when no fences are found.
    /// </summary>
    public static string ExtractJsonBlock(string text)
    {
        Match match = JsonFencePattern().Match(text);
        if (match.Success)
        {
            return match.Groups["content"].Value.Trim();
        }

        match = GenericFencePattern().Match(text);
        if (match.Success)
        {
            return match.Groups["content"].Value.Trim();
        }

        return text;
    }

    /// <summary>
    /// Extracts the first code block from markdown-fenced text
    /// (<c>```csharp … ```</c> or <c>``` … ```</c>).
    /// Returns the original text when no fences are found.
    /// </summary>
    public static string ExtractCodeBlock(string text)
    {
        Match match = CSharpFencePattern().Match(text);
        if (match.Success)
        {
            return match.Groups["content"].Value.Trim();
        }

        match = GenericFencePattern().Match(text);
        if (match.Success)
        {
            return match.Groups["content"].Value.Trim();
        }

        return text;
    }

    [GeneratedRegex(@":\s*-?(?:NaN|Infinity)\b", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex NaNPattern();

    [GeneratedRegex(@"```json\s*\n(?<content>.*?)\n\s*```", RegexOptions.Singleline | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex JsonFencePattern();

    [GeneratedRegex(@"```csharp\s*\n(?<content>.*?)\n\s*```", RegexOptions.Singleline | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex CSharpFencePattern();

    [GeneratedRegex(@"```\s*\n(?<content>.*?)\n\s*```", RegexOptions.Singleline | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex GenericFencePattern();
}
