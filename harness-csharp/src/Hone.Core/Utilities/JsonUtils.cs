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
    /// attempts to find an unfenced JSON object/array, and finally
    /// returns the original text when nothing matches.
    /// </summary>
    public static string ExtractJsonBlock(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

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

        // Fallback: find the first '{' or '[' that starts a JSON object/array
        // and match it to the last corresponding '}' or ']'.
        return TryExtractUnfencedJson(text) ?? text;
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

    /// <summary>
    /// Attempts to locate an unfenced JSON object or array in the text by finding the
    /// first <c>{</c> or <c>[</c> and matching it to the last corresponding <c>}</c> or <c>]</c>.
    /// </summary>
    private static string? TryExtractUnfencedJson(string text)
    {
        int openBrace = text.IndexOf('{', StringComparison.Ordinal);
        int openBracket = text.IndexOf('[', StringComparison.Ordinal);

        int start;
        char closeChar;
        if (openBrace < 0 && openBracket < 0)
        {
            return null;
        }

        if (openBrace < 0)
        {
            start = openBracket;
            closeChar = ']';
        }
        else if (openBracket < 0 || openBrace < openBracket)
        {
            start = openBrace;
            closeChar = '}';
        }
        else
        {
            start = openBracket;
            closeChar = ']';
        }

        int end = text.LastIndexOf(closeChar);
        if (end <= start)
        {
            return null;
        }

        return text[start..(end + 1)];
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
