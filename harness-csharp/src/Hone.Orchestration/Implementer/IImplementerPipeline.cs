namespace Hone.Orchestration.Implementer;

internal interface IImplementerPipeline
{
    public Task<FixStepResult> InvokeImplementerAgentAsync(FixStepInput input, CancellationToken ct);
    public Task<ApplyStepResult> ApplySuggestionAsync(ApplyStepInput input, CancellationToken ct);
    public Task<BuildStepResult> BuildProjectAsync(BuildStepInput input, CancellationToken ct);
    public Task<TestStepResult> RunTestsAsync(TestStepInput input, CancellationToken ct);
    public Task RevertForRetryAsync(RevertInput input, CancellationToken ct);
    public Task<int> GetDiffLineCountAsync(string workingDir, CancellationToken ct);
    public Task<IReadOnlyList<string>> GetChangedFilesAsync(string workingDir, CancellationToken ct);
}
