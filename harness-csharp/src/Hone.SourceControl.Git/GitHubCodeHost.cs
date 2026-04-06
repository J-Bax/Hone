using System.Text.Json;
using Hone.Core.Contracts;
using Hone.Core.Models;

namespace Hone.SourceControl.Git;

/// <summary>
/// GitHub-based implementation of <see cref="ICodeHost"/> that shells out
/// to <c>git</c> and the <c>gh</c> CLI via <see cref="IProcessRunner"/>.
/// </summary>
public sealed class GitHubCodeHost(IProcessRunner processRunner) : ICodeHost
{
    /// <inheritdoc />
    public async Task<PushResult> PushBranchAsync(string workingDir, string branch, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDir);
        ArgumentException.ThrowIfNullOrEmpty(branch);

        ProcessResult result = await processRunner.RunAsync(
            "git",
            ["push", "-u", "origin", branch],
            workingDir,
            timeout: null,
            ct).ConfigureAwait(false);

        return new PushResult(result.Success, result.Output);
    }

    /// <inheritdoc />
    public async Task<PullRequestResult> CreatePullRequestAsync(CreatePrOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        ProcessResult result = await processRunner.RunAsync(
            "gh",
            [
                "pr", "create",
                "--base", options.BaseBranch,
                "--head", options.HeadBranch,
                "--title", options.Title,
                "--body", options.Body,
            ],
            options.WorkingDirectory,
            timeout: null,
            ct).ConfigureAwait(false);

        if (!result.Success)
        {
            return new PullRequestResult(Success: false, PrNumber: null, PrUrl: null);
        }

        string output = result.Output.Trim();
        if (!Uri.TryCreate(output, UriKind.Absolute, out Uri? prUrl))
        {
            return new PullRequestResult(Success: false, PrNumber: null, PrUrl: null);
        }

        // Extract PR number from the last path segment of the URL
        // e.g., https://github.com/owner/repo/pull/42 → 42
        string lastSegment = prUrl.Segments[^1].TrimEnd('/');
        if (!int.TryParse(lastSegment, System.Globalization.CultureInfo.InvariantCulture, out int prNumber))
        {
            return new PullRequestResult(Success: false, PrNumber: null, PrUrl: prUrl);
        }

        return new PullRequestResult(Success: true, PrNumber: prNumber, PrUrl: prUrl);
    }

    /// <inheritdoc />
    public async Task<PullRequestStatus> GetPullRequestStatusAsync(int prNumber, CancellationToken ct = default)
    {
        ProcessResult result = await processRunner.RunAsync(
            "gh",
            ["pr", "view", prNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "state,mergedAt"],
            workingDirectory: null,
            timeout: null,
            ct).ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Failed to get PR #{prNumber} status: {result.Output}");
        }

        using var doc = JsonDocument.Parse(result.Output);
        JsonElement root = doc.RootElement;

        string state = root.GetProperty("state").GetString() ?? "UNKNOWN";
        bool merged = root.TryGetProperty("mergedAt", out JsonElement mergedAt)
            && mergedAt.ValueKind != JsonValueKind.Null;

        return new PullRequestStatus(prNumber, state, merged);
    }
}
