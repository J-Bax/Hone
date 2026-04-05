using System.Globalization;

namespace Hone.Diagnostics.Collectors;

/// <summary>
/// Parses PerfView's SaveCPUStacksAsCsv / SaveManagedHeapAllocStacksAsCsv
/// CSV output into folded-stack format (semicolon-delimited frames + count).
/// </summary>
internal static class FoldedStackParser
{
    /// <summary>
    /// Modules known to be unrelated noise processes. Used when filtering
    /// an unfiltered PerfView CSV export to approximate process-scoped stacks.
    /// </summary>
    private static readonly HashSet<string> ExcludedModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "k6", "conhost", "searchfilterhost", "searchindexer",
        "teracopyservice", "explorer", "dwm", "csrss", "wininit", "lsass",
    };

    /// <summary>
    /// Parses CSV content into folded-stack lines sorted by count descending.
    /// </summary>
    /// <param name="csvContent">Raw CSV text from PerfView export.</param>
    /// <param name="filterExcludedModules">
    /// When <c>true</c>, rows whose module prefix is in <see cref="ExcludedModules"/>
    /// are excluded. Used for unfiltered exports where all processes are mixed.
    /// </param>
    /// <param name="maxStacks">Maximum number of stack lines to return.</param>
    /// <returns>Folded stack lines in the format <c>Name count</c>.</returns>
    public static IReadOnlyList<string> Parse(
        string csvContent,
        bool filterExcludedModules,
        int maxStacks)
    {
        if (string.IsNullOrWhiteSpace(csvContent))
        {
            return [];
        }

        string[] lines = csvContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            return [];
        }

        IReadOnlyList<string> header = ParseCsvLine(lines[0].TrimEnd('\r'));
        int nameIndex = FindColumnIndex(header, "Name", "stack", "call");
        int countIndex = FindColumnIndex(header, "Exc", "count", "sample", "weight");

        if (nameIndex < 0)
        {
            nameIndex = 0;
        }

        if (countIndex < 0)
        {
            countIndex = FindColumnIndex(header, "Inc");
            if (countIndex < 0)
            {
                countIndex = Math.Min(1, header.Count - 1);
            }
        }

        var entries = new List<(string Name, int Count)>();

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            IReadOnlyList<string> fields = ParseCsvLine(line);
            if (fields.Count <= Math.Max(nameIndex, countIndex))
            {
                continue;
            }

            string name = fields[nameIndex];
            if (string.IsNullOrEmpty(name) ||
                string.Equals(name, "ROOT", StringComparison.Ordinal))
            {
                continue;
            }

            if (filterExcludedModules)
            {
                string module = name.Split('!')[0];
                if (ExcludedModules.Contains(module))
                {
                    continue;
                }
            }

            string rawCount = fields[countIndex]
                .Replace(",", "", StringComparison.Ordinal)
                .Replace(" ", "", StringComparison.Ordinal);

            if (double.TryParse(rawCount, NumberStyles.Float, CultureInfo.InvariantCulture, out double count) &&
                count > 0)
            {
                entries.Add((name, (int)count));
            }
        }

        return
        [
            .. entries
                .OrderByDescending(e => e.Count)
                .Take(maxStacks)
                .Select(e => $"{e.Name} {e.Count}"),
        ];
    }

    /// <summary>
    /// Finds a column index by exact name match, falling back to substring match.
    /// </summary>
    private static int FindColumnIndex(
        IReadOnlyList<string> header,
        string exactName,
        params string[] substringFallbacks)
    {
        for (int i = 0; i < header.Count; i++)
        {
            if (string.Equals(header[i], exactName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        foreach (string fallback in substringFallbacks)
        {
            for (int i = 0; i < header.Count; i++)
            {
                if (header[i].Contains(fallback, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Parses a single CSV line into fields, handling quoted values.
    /// </summary>
    internal static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        int i = 0;

        while (i < line.Length)
        {
            if (line[i] == '"')
            {
                // Quoted field — find closing quote (handle escaped "")
                int start = i + 1;
                int end = start;

                while (end < line.Length)
                {
                    if (line[end] == '"')
                    {
                        if (end + 1 < line.Length && line[end + 1] == '"')
                        {
                            end += 2; // skip escaped quote
                        }
                        else
                        {
                            break;
                        }
                    }
                    else
                    {
                        end++;
                    }
                }

                fields.Add(line[start..end].Replace("\"\"", "\"", StringComparison.Ordinal));
                i = end + 1; // skip closing quote
                if (i < line.Length && line[i] == ',')
                {
                    i++; // skip comma
                }
            }
            else
            {
                // Unquoted field
                int end = line.IndexOf(',', i);
                if (end < 0)
                {
                    end = line.Length;
                }

                fields.Add(line[i..end]);
                i = end + 1;
            }
        }

        return fields;
    }
}
