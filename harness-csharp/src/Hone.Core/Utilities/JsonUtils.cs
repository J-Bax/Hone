using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hone.Core.Utilities;

/// <summary>
/// JSON pre-processing helpers for sanitising k6 output and extracting
/// JSON/code blocks from noisy LLM responses.
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
    /// Extracts the largest valid JSON object/array embedded anywhere in
    /// the text. This tolerates markdown fences, prefixed chatter, and
    /// inline brace noise before the real payload.
    /// </summary>
    public static string ExtractJsonBlock(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return TryExtractLargestValidJson(text) ?? text;
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
    /// Attempts to locate the largest valid JSON object or array anywhere in
    /// the text by scanning for balanced JSON fragments and validating each
    /// candidate with <see cref="JsonDocument"/>.
    /// </summary>
    private static string? TryExtractLargestValidJson(string text)
    {
        string? bestCandidate = null;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('{' or '['))
            {
                continue;
            }

            if (!TryExtractBalancedJson(text, i, out string? candidate)
                || candidate is null
                || !IsValidJson(candidate))
            {
                continue;
            }

            if (bestCandidate is null || candidate.Length > bestCandidate.Length)
            {
                bestCandidate = candidate;
            }
        }

        return bestCandidate;
    }

    private static bool TryExtractBalancedJson(string text, int startIndex, out string? candidate)
    {
        candidate = null;

        var stack = new Stack<char>();
        bool inString = false;
        bool escaping = false;

        for (int i = startIndex; i < text.Length; i++)
        {
            char ch = text[i];

            if (inString)
            {
                if (escaping)
                {
                    escaping = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaping = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch is '{' or '[')
            {
                stack.Push(ch);
                continue;
            }

            if (ch is not ('}' or ']'))
            {
                continue;
            }

            if (stack.Count == 0 || !IsMatchingJsonDelimiter(stack.Peek(), ch))
            {
                return false;
            }

            _ = stack.Pop();
            if (stack.Count == 0)
            {
                candidate = text[startIndex..(i + 1)];
                return true;
            }
        }

        return false;
    }

    private static bool IsValidJson(string candidate)
    {
        try
        {
            using var _ = JsonDocument.Parse(SanitizeNaN(candidate));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsMatchingJsonDelimiter(char open, char close)
    {
        return open switch
        {
            '{' => close == '}',
            '[' => close == ']',
            _ => false,
        };
    }

    [GeneratedRegex(@":\s*-?(?:NaN|Infinity)\b", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex NaNPattern();

    [GeneratedRegex(@"```csharp\s*\n(?<content>.*?)\n\s*```", RegexOptions.Singleline | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex CSharpFencePattern();

    [GeneratedRegex(@"```\s*\n(?<content>.*?)\n\s*```", RegexOptions.Singleline | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex GenericFencePattern();
}
