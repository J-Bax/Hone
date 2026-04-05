using System.Globalization;
using System.Text;

namespace Hone.SourceControl.PullRequests;

/// <summary>
/// Builds a markdown "stack note" showing the chain of PRs in a stacked-diff series.
/// Replaces <c>Build-StackNote</c> from HoneHelpers.psm1.
/// </summary>
public static class StackNoteBuilder
{
    private const string CheckMark = "✓";
    private const string CrossMark = "✗";

    /// <summary>
    /// Builds the stack note markdown for a PR body.
    /// </summary>
    /// <param name="options">Options describing the PR chain, failed experiments, and current experiment.</param>
    /// <returns>
    /// A markdown string showing the PR chain, or an empty string if
    /// <see cref="StackNoteOptions.PrChain"/> is empty.
    /// </returns>
    public static string Build(StackNoteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.PrChain.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();

        // Stack line: `baseBranch` → PR #N (experiment-N) ✓ → ... → **this PR** (experiment-K) TAG
        sb.Append(CultureInfo.InvariantCulture, $"\n**Stack:** `{options.BaseBranch}`");

        foreach (PrChainEntry entry in options.PrChain)
        {
            string marker = string.Equals(entry.Outcome, "improved", StringComparison.OrdinalIgnoreCase)
                ? CheckMark
                : CrossMark;

            sb.Append(CultureInfo.InvariantCulture, $" → PR #{entry.Number} (experiment-{entry.Experiment}) {marker}");
        }

        sb.Append(CultureInfo.InvariantCulture, $" → **this PR** (experiment-{options.Experiment}) {options.OutcomeTag}");

        // Base line
        sb.AppendLine();
        sb.AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"**Base:** `{options.BaseBranch}` (review only the incremental change)");

        // Failed experiments note — only those between the last PR in the chain and the current experiment
        if (options.FailedExperiments.Count > 0)
        {
            int lastChainExperiment = options.PrChain[^1].Experiment;
            HashSet<int> prExperimentNums = [.. options.PrChain.Select(p => p.Experiment)];

            var failedBetween = options.FailedExperiments
                .Where(f => f.Experiment > lastChainExperiment
                         && f.Experiment < options.Experiment
                         && !prExperimentNums.Contains(f.Experiment))
                .ToList();

            if (failedBetween.Count > 0)
            {
                string failedList = string.Join(
                    ", ",
                    failedBetween.Select(f => string.Create(
                        CultureInfo.InvariantCulture,
                        $"{f.Experiment} ({f.Reason})")));

                sb.AppendLine();
                sb.AppendLine();
                sb.Append(CultureInfo.InvariantCulture, $"> **Note:** Experiments {failedList} were attempted but did not produce branches.");
            }
        }

        sb.AppendLine();

        return sb.ToString();
    }
}
