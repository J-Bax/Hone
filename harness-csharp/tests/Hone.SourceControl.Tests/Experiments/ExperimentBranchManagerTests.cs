using FluentAssertions;
using Hone.Core.Contracts;
using Hone.SourceControl.Experiments;
using Hone.TestInfrastructure;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Hone.SourceControl.Tests.Experiments;

public sealed class ExperimentBranchManagerTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private readonly IVersionControl _versionControl = Substitute.For<IVersionControl>();

    private ExperimentBranchManager CreateSut() => new(_versionControl);

    private ApplySuggestionOptions CreateApplyOptions(
        int experiment = 1,
        string? targetFilePath = null,
        string suggestionContent = "new content",
        string description = "optimize loop",
        string baseBranch = "main",
        string? workingDir = null)
    {
        workingDir ??= TempDir;
        targetFilePath ??= Path.Combine(workingDir, "src", "Program.cs");

        // Ensure the parent directory exists so the file can be written
        string? dir = Path.GetDirectoryName(targetFilePath);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        return new ApplySuggestionOptions(
            WorkingDir: workingDir,
            BaseBranch: baseBranch,
            Experiment: experiment,
            TargetFilePath: targetFilePath,
            SuggestionContent: suggestionContent,
            Description: description);
    }

    private RevertExperimentOptions CreateRevertOptions(
        int experiment = 1,
        string? targetFilePath = null,
        string originalContent = "original content",
        string outcome = "regressed",
        string branchName = "hone/experiment-1",
        string? workingDir = null)
    {
        workingDir ??= TempDir;
        targetFilePath ??= Path.Combine(workingDir, "src", "Program.cs");

        string? dir = Path.GetDirectoryName(targetFilePath);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        return new RevertExperimentOptions(
            WorkingDir: workingDir,
            Experiment: experiment,
            BranchName: branchName,
            TargetFilePath: targetFilePath,
            OriginalContent: originalContent,
            Outcome: outcome);
    }

    // ───── ApplySuggestion tests ─────

    [Fact]
    public async Task ApplySuggestion_CreatesExperimentBranch()
    {
        ApplySuggestionOptions options = CreateApplyOptions(experiment: 3);
        ExperimentBranchManager sut = CreateSut();

        ApplySuggestionResult result = await sut.ApplySuggestionAsync(options);

        _ = result.Success.Should().BeTrue();
        _ = result.BranchName.Should().Be("hone/experiment-3");
        await _versionControl.Received(1).CheckoutAsync(
            options.WorkingDir, "hone/experiment-3", create: true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplySuggestion_WritesFileContent()
    {
        const string Expected = "optimized code here";
        ApplySuggestionOptions options = CreateApplyOptions(suggestionContent: Expected);
        ExperimentBranchManager sut = CreateSut();

        _ = await sut.ApplySuggestionAsync(options);

        string actual = await File.ReadAllTextAsync(options.TargetFilePath);
        _ = actual.Should().Be(Expected);
    }

    [Fact]
    public async Task ApplySuggestion_CommitsWithMessage()
    {
        ApplySuggestionOptions options = CreateApplyOptions(experiment: 5, description: "inline cache");
        ExperimentBranchManager sut = CreateSut();

        _ = await sut.ApplySuggestionAsync(options);

        await _versionControl.Received(1).CommitAsync(
            options.WorkingDir,
            "hone(experiment-5): inline cache",
            Arg.Is<IEnumerable<string>>(p => p.Contains(options.TargetFilePath)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplySuggestion_BlocksPathTraversal()
    {
        string outsidePath = Path.Combine(TempDir, "..", "evil.cs");
        ApplySuggestionOptions options = CreateApplyOptions(targetFilePath: outsidePath);
        ExperimentBranchManager sut = CreateSut();

        ApplySuggestionResult result = await sut.ApplySuggestionAsync(options);

        _ = result.Success.Should().BeFalse();
        _ = result.ErrorMessage.Should().Contain("Path traversal blocked");
    }

    // ───── RevertExperiment tests ─────

    [Fact]
    public async Task RevertExperiment_VerifiesBranch()
    {
        RevertExperimentOptions options = CreateRevertOptions(branchName: "hone/experiment-1");
        _ = _versionControl.GetCurrentBranchAsync(options.WorkingDir, Arg.Any<CancellationToken>())
            .Returns("some-other-branch");
        ExperimentBranchManager sut = CreateSut();

        RevertExperimentResult result = await sut.RevertExperimentAsync(options);

        _ = result.Success.Should().BeFalse();
        _ = result.ErrorMessage.Should().Contain("Expected branch 'hone/experiment-1'");
        _ = result.ErrorMessage.Should().Contain("some-other-branch");
    }

    [Fact]
    public async Task RevertExperiment_RevertsLastCommit()
    {
        RevertExperimentOptions options = CreateRevertOptions();
        _ = _versionControl.GetCurrentBranchAsync(options.WorkingDir, Arg.Any<CancellationToken>())
            .Returns(options.BranchName);
        ExperimentBranchManager sut = CreateSut();

        _ = await sut.RevertExperimentAsync(options);

        await _versionControl.Received(1).RevertLastCommitAsync(
            options.WorkingDir, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevertExperiment_WritesOriginalContent()
    {
        const string Original = "original source code";
        RevertExperimentOptions options = CreateRevertOptions(originalContent: Original);
        _ = _versionControl.GetCurrentBranchAsync(options.WorkingDir, Arg.Any<CancellationToken>())
            .Returns(options.BranchName);
        ExperimentBranchManager sut = CreateSut();

        _ = await sut.RevertExperimentAsync(options);

        string actual = await File.ReadAllTextAsync(options.TargetFilePath);
        _ = actual.Should().Be(Original);
    }

    [Fact]
    public async Task RevertExperiment_CommitsRevertMessage()
    {
        RevertExperimentOptions options = CreateRevertOptions(experiment: 2, outcome: "regressed", branchName: "hone/experiment-2");
        _ = _versionControl.GetCurrentBranchAsync(options.WorkingDir, Arg.Any<CancellationToken>())
            .Returns(options.BranchName);
        ExperimentBranchManager sut = CreateSut();

        _ = await sut.RevertExperimentAsync(options);

        await _versionControl.Received(1).CommitAsync(
            options.WorkingDir,
            "hone(experiment-2): revert — regressed",
            Arg.Is<IEnumerable<string>>(p => p.Contains(options.TargetFilePath)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevertExperiment_BlocksPathTraversal()
    {
        string outsidePath = Path.Combine(TempDir, "..", "evil.cs");
        RevertExperimentOptions options = CreateRevertOptions(targetFilePath: outsidePath);
        _ = _versionControl.GetCurrentBranchAsync(options.WorkingDir, Arg.Any<CancellationToken>())
            .Returns(options.BranchName);
        ExperimentBranchManager sut = CreateSut();

        RevertExperimentResult result = await sut.RevertExperimentAsync(options);

        _ = result.Success.Should().BeFalse();
        _ = result.ErrorMessage.Should().Contain("Path traversal blocked");
    }
}
