namespace Hone.Core.Utilities;

/// <summary>
/// String manipulation helpers.
/// </summary>
public static class StringUtils
{
    private const char Ellipsis = '\u2026';

    /// <summary>
    /// Truncates a string at the last word boundary before <paramref name="maxLength"/>,
    /// appending "\u2026" when truncated.
    /// </summary>
    /// <remarks>
    /// The ellipsis is appended <em>after</em> the truncation point, so the returned
    /// string can be up to <c>maxLength + 1</c> characters when truncation occurs.
    /// </remarks>
    public static string? Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        string truncated = text[..maxLength];
        int lastSpace = truncated.LastIndexOf(' ');

        if (lastSpace > (int)(maxLength * 0.5))
        {
            return string.Concat(truncated.AsSpan(0, lastSpace), Ellipsis.ToString());
        }

        return truncated + Ellipsis;
    }
}
