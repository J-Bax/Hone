namespace Hone.Orchestration.Implementer;

/// <summary>
/// Abstracts the external operations called during the iterative fix cycle.
/// Each method corresponds to a script invoked by <c>Invoke-IterativeFix.ps1</c>.
/// </summary>
internal interface IImplementerPipeline
{
    /// <summary>Invokes the fix agent to produce a code block suggestion.</summary>
    public Task<FixStepResult> InvokeFixAgentAsync(FixStepInput input, CancellationToken ct);

    /// <summary>Applies the suggested code change and commits to a branch.</summary>
    public Task<ApplyStepResult> ApplySuggestionAsync(ApplyStepInput input, CancellationToken ct);

    /// <summary>Builds the project and returns the outcome.</summary>
    public Task<BuildStepResult> BuildProjectAsync(BuildStepInput input, CancellationToken ct);

    /// <summary>Runs end-to-end tests and returns the outcome.</summary>
    public Task<TestStepResult> RunTestsAsync(TestStepInput input, CancellationToken ct);

    /// <summary>Reverts the last applied change so the next attempt starts clean.</summary>
    public Task RevertForRetryAsync(RevertInput input, CancellationToken ct);

    /// <summary>Returns the total added+removed line count of the latest commit.</summary>
    public Task<int> GetDiffLineCountAsync(string workingDir, CancellationToken ct);

    /// <summary>Returns the list of files changed in the latest commit.</summary>
    public Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDir, CancellationToken ct);
}
