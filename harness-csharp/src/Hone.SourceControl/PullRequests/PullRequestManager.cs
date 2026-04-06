using System.Globalization;

using Hone.Core.Contracts;
using Hone.Core.Utilities;

namespace Hone.SourceControl.PullRequests;

/// <summary>
/// Creates experiment pull requests on a code host.
/// </summary>
public sealed class PullRequestManager(ICodeHost codeHost)
{
    private const int MaxDescriptionLength = 120;

    /// <summary>
    /// Creates a pull request for a completed experiment.
    /// </summary>
    /// <param name="options">Options describing the experiment and PR content.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="PullRequestResult"/> indicating success/failure and the PR URL/number.</returns>
    public async Task<PullRequestResult> CreateExperimentPrAsync(
        CreateExperimentPrOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        string outcomeTag = string.Equals(options.Outcome, "improved", StringComparison.OrdinalIgnoreCase)
            ? "ACCEPTED"
            : "REJECTED";

        string dryRunPrefix = options.IsDryRun ? "[DRY RUN] " : string.Empty;

        string truncatedDescription = StringUtils.Truncate(options.Description, MaxDescriptionLength)
            ?? string.Empty;

        string title = string.Create(
            CultureInfo.InvariantCulture,
            $"{dryRunPrefix}hone(experiment-{options.Experiment})[{outcomeTag}]: {truncatedDescription}");

        var prOptions = new CreatePrOptions(
            BaseBranch: options.BaseBranch,
            HeadBranch: options.BranchName,
            Title: title,
            Body: options.Body,
            WorkingDirectory: options.WorkingDirectory);

        return await codeHost.CreatePullRequestAsync(prOptions, ct).ConfigureAwait(false);
    }
}
