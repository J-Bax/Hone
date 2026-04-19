using FluentAssertions;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.TestInfrastructure;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Hone.SourceControl.Git.Tests;

public sealed class GitVersionControlTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private static readonly string[] RevParseArgs = ["rev-parse", "--abbrev-ref", "HEAD"];
    private static readonly string[] RevParseHeadArgs = ["rev-parse", "HEAD"];
    private static readonly string[] ShowRefFeatureArgs = ["show-ref", "--verify", "--quiet", "refs/heads/feature"];
    private static readonly string[] StatusPorcelainArgs = ["status", "--porcelain", "--untracked-files=normal"];
    private static readonly string[] StatusPorcelainZArgs =
        ["status", "--porcelain=v1", "-z", "--untracked-files=normal", "--no-renames"];
    private static readonly string[] CheckoutFeatureArgs = ["checkout", "feature"];
    private static readonly string[] CheckoutCreateFeatureArgs = ["checkout", "-b", "feature"];
    private static readonly string[] AddFilesArgs = ["add", "--", "file1.cs", "file2.cs"];
    private static readonly string[] CommitArgs = ["commit", "--no-gpg-sign", "-m", "fix bug"];
    private static readonly string[] DiffArgs = ["diff"];
    private static readonly string[] DiffThreeDotArgs = ["diff", "--", "main...HEAD"];
    private static readonly string[] TouchedTrackedDiffArgs =
        ["diff", "--name-only", "-z", "--no-renames", "--diff-filter=ACDMRTUXB", "main...HEAD", "--"];
    private static readonly string[] RestoreTrackedPathsArgs =
    [
        "restore",
        "--source",
        "main",
        "--staged",
        "--worktree",
        "--",
        "file1.cs",
        "file2.cs",
    ];
    private static readonly string[] RemoveUntrackedPathsArgs =
    [
        "clean",
        "-f",
        "-d",
        "-x",
        "--",
        "artifact.txt",
        "generated",
    ];
    private static readonly string[] ResetSoftArgs = ["reset", "--soft", "HEAD~1"];

    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();

    private GitVersionControl CreateSut() => new(_processRunner);

    private void SetupProcessRunner(bool success, string output = "", int exitCode = 0)
    {
        _ = _processRunner.RunAsync(
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string?>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(Success: success, Output: output, ExitCode: exitCode, TimedOut: false));
    }

    [Fact]
    public async Task GetCurrentBranch_CallsGitRevParse_ReturnsTrimmedOutput()
    {
        SetupProcessRunner(success: true, output: "  main\n");
        GitVersionControl sut = CreateSut();

        string branch = await sut.GetCurrentBranchAsync("/repo");

        _ = branch.Should().Be("main");
        _ = await _processRunner.Received(1).RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(RevParseArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCurrentBranch_Failure_ThrowsInvalidOperation()
    {
        SetupProcessRunner(success: false, output: "not a git repo");
        GitVersionControl sut = CreateSut();

        Func<Task> act = () => sut.GetCurrentBranchAsync("/repo");

        _ = (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*not a git repo*");
    }

    [Fact]
    public async Task GetHeadSha_CallsGitRevParseHead_ReturnsTrimmedOutput()
    {
        SetupProcessRunner(success: true, output: "  abc123\n");
        GitVersionControl sut = CreateSut();

        string sha = await sut.GetHeadShaAsync("/repo");

        _ = sha.Should().Be("abc123");
        _ = await _processRunner.Received(1).RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(RevParseHeadArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetHeadSha_Failure_ThrowsInvalidOperation()
    {
        SetupProcessRunner(success: false, output: "not a git repo");
        GitVersionControl sut = CreateSut();

        Func<Task> act = () => sut.GetHeadShaAsync("/repo");

        _ = (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*not a git repo*");
    }

    [Fact]
    public async Task LocalBranchExists_WhenBranchExists_ReturnsTrue()
    {
        SetupProcessRunner(success: true);
        GitVersionControl sut = CreateSut();

        bool exists = await sut.LocalBranchExistsAsync("/repo", "feature");

        _ = exists.Should().BeTrue();
        _ = await _processRunner.Received(1).RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(ShowRefFeatureArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LocalBranchExists_WhenBranchIsMissing_ReturnsFalse()
    {
        SetupProcessRunner(success: false, exitCode: 1);
        GitVersionControl sut = CreateSut();

        bool exists = await sut.LocalBranchExistsAsync("/repo", "feature");

        _ = exists.Should().BeFalse();
    }

    [Fact]
    public async Task LocalBranchExists_WhenGitFails_ThrowsInvalidOperation()
    {
        SetupProcessRunner(success: false, output: "fatal: not a git repo", exitCode: 128);
        GitVersionControl sut = CreateSut();

        Func<Task> act = async () => _ = await sut.LocalBranchExistsAsync("/repo", "feature").ConfigureAwait(false);

        _ = (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*feature*")
            .WithMessage("*not a git repo*");
    }

    [Fact]
    public async Task IsWorkingTreeClean_WhenStatusIsEmpty_ReturnsTrue()
    {
        SetupProcessRunner(success: true, output: " \n");
        GitVersionControl sut = CreateSut();

        bool isClean = await sut.IsWorkingTreeCleanAsync("/repo");

        _ = isClean.Should().BeTrue();
        _ = await _processRunner.Received(1).RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(StatusPorcelainArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsWorkingTreeClean_WhenStatusHasEntries_ReturnsFalse()
    {
        SetupProcessRunner(success: true, output: " M file1.cs\n?? artifact.txt\n");
        GitVersionControl sut = CreateSut();

        bool isClean = await sut.IsWorkingTreeCleanAsync("/repo");

        _ = isClean.Should().BeFalse();
    }

    [Fact]
    public async Task IsWorkingTreeClean_WhenOnlyIgnoredPathsAreDirty_ReturnsTrue()
    {
        SetupProcessRunner(
            success: true,
            output: "?? hone-results/run-metadata.json\0 M hone-results/metadata/run-state.json\0");
        GitVersionControl sut = CreateSut();

        bool isClean = await sut.IsWorkingTreeCleanAsync("/repo", ["hone-results"]);

        _ = isClean.Should().BeTrue();
        _ = await _processRunner.Received(1).RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(StatusPorcelainZArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsWorkingTreeClean_WhenNonIgnoredPathsRemain_ReturnsFalse()
    {
        SetupProcessRunner(
            success: true,
            output: "?? hone-results/run-metadata.json\0 M src/Service.cs\0");
        GitVersionControl sut = CreateSut();

        bool isClean = await sut.IsWorkingTreeCleanAsync("/repo", ["hone-results"]);

        _ = isClean.Should().BeFalse();
    }

    [Fact]
    public async Task IsWorkingTreeClean_Failure_ThrowsInvalidOperation()
    {
        SetupProcessRunner(success: false, output: "fatal: not a git repo");
        GitVersionControl sut = CreateSut();

        Func<Task> act = async () => _ = await sut.IsWorkingTreeCleanAsync("/repo").ConfigureAwait(false);

        _ = (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*not a git repo*");
    }

    [Fact]
    public async Task Checkout_WithoutCreate_CallsGitCheckout()
    {
        SetupProcessRunner(success: true);
        GitVersionControl sut = CreateSut();

        await sut.CheckoutAsync("/repo", "feature");

        _ = await _processRunner.Received(1).RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(CheckoutFeatureArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Checkout_WithCreate_CallsGitCheckoutB()
    {
        SetupProcessRunner(success: true);
        GitVersionControl sut = CreateSut();

        await sut.CheckoutAsync("/repo", "feature", create: true);

        _ = await _processRunner.Received(1).RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(CheckoutCreateFeatureArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Checkout_Failure_ThrowsInvalidOperation()
    {
        SetupProcessRunner(success: false, output: "branch not found");
        GitVersionControl sut = CreateSut();

        Func<Task> act = () => sut.CheckoutAsync("/repo", "missing");

        _ = (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*missing*");
    }

    [Fact]
    public async Task Commit_WithPaths_StagesAndCommits()
    {
        SetupProcessRunner(success: true);
        GitVersionControl sut = CreateSut();

        await sut.CommitAsync("/repo", "fix bug", paths: ["file1.cs", "file2.cs"]);

        _ = await _processRunner.Received(1).RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(AddFilesArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        _ = await _processRunner.Received(1).RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(CommitArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Commit_WithoutPaths_CommitsOnly()
    {
        SetupProcessRunner(success: true);
        GitVersionControl sut = CreateSut();

        await sut.CommitAsync("/repo", "fix bug");

        _ = await _processRunner.DidNotReceive().RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.First() == "add"),
            Arg.Any<string?>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());

        _ = await _processRunner.Received(1).RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(CommitArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDiff_WithoutBaseBranch_CallsGitDiff()
    {
        SetupProcessRunner(success: true, output: "diff content");
        GitVersionControl sut = CreateSut();

        string diff = await sut.GetDiffAsync("/repo");

        _ = diff.Should().Be("diff content");
        _ = await _processRunner.Received(1).RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(DiffArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDiff_WithBaseBranch_CallsGitDiffThreeDot()
    {
        SetupProcessRunner(success: true, output: "diff content");
        GitVersionControl sut = CreateSut();

        string diff = await sut.GetDiffAsync("/repo", baseBranch: "main");

        _ = diff.Should().Be("diff content");
        _ = await _processRunner.Received(1).RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(DiffThreeDotArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTouchedTrackedPaths_ReturnsUnionOfDiffAndStatusPaths()
    {
        _ = _processRunner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<string?>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ProcessResult(
                    Success: true,
                    Output: "src/Changed.cs\0src/Added.cs\0",
                    ExitCode: 0,
                    TimedOut: false),
                new ProcessResult(
                    Success: true,
                    Output: " M src/Dirty.cs\0D  src/Deleted.cs\0?? scratch.txt\0",
                    ExitCode: 0,
                    TimedOut: false));
        GitVersionControl sut = CreateSut();

        IReadOnlyList<string> paths = await sut.GetTouchedTrackedPathsAsync("/repo", "main");

        _ = paths.Should().Equal("src/Added.cs", "src/Changed.cs", "src/Deleted.cs", "src/Dirty.cs");
        _ = await _processRunner.Received(1).RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(TouchedTrackedDiffArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
        _ = await _processRunner.Received(1).RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(StatusPorcelainZArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTouchedTrackedPaths_WhenDiffFails_ThrowsInvalidOperation()
    {
        _ = _processRunner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<string?>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(
                Success: false,
                Output: "fatal: diff failed",
                ExitCode: 1,
                TimedOut: false));
        GitVersionControl sut = CreateSut();

        Func<Task> act = () => sut.GetTouchedTrackedPathsAsync("/repo", "main");

        _ = (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*main*")
            .WithMessage("*diff failed*");
    }

    [Fact]
    public async Task GetUntrackedPaths_ReturnsOnlyUntrackedEntries()
    {
        SetupProcessRunner(success: true, output: "?? artifact.txt\0 M src/Service.cs\0?? generated/output.json\0");
        GitVersionControl sut = CreateSut();

        IReadOnlyList<string> paths = await sut.GetUntrackedPathsAsync("/repo");

        _ = paths.Should().Equal("artifact.txt", "generated/output.json");
        _ = await _processRunner.Received(1).RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(StatusPorcelainZArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUntrackedPaths_Failure_ThrowsInvalidOperation()
    {
        SetupProcessRunner(success: false, output: "fatal: status failed");
        GitVersionControl sut = CreateSut();

        Func<Task> act = () => sut.GetUntrackedPathsAsync("/repo");

        _ = (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*status failed*");
    }

    [Fact]
    public async Task RestoreTrackedPaths_WithPaths_CallsGitRestore()
    {
        SetupProcessRunner(success: true);
        GitVersionControl sut = CreateSut();

        await sut.RestoreTrackedPathsAsync("/repo", "main", ["file1.cs", "file2.cs"]);

        _ = await _processRunner.Received(1).RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(RestoreTrackedPathsArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreTrackedPaths_WithoutPaths_DoesNothing()
    {
        GitVersionControl sut = CreateSut();

        await sut.RestoreTrackedPathsAsync("/repo", "main", []);

        _ = await _processRunner.DidNotReceive().RunAsync(
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string?>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreTrackedPaths_Failure_ThrowsInvalidOperation()
    {
        SetupProcessRunner(success: false, output: "pathspec failed");
        GitVersionControl sut = CreateSut();

        Func<Task> act = () => sut.RestoreTrackedPathsAsync("/repo", "main", ["file1.cs"]);

        _ = (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*main*")
            .WithMessage("*pathspec failed*");
    }

    [Fact]
    public async Task RemoveUntrackedPaths_WithPaths_CallsGitClean()
    {
        SetupProcessRunner(success: true);
        GitVersionControl sut = CreateSut();

        await sut.RemoveUntrackedPathsAsync("/repo", ["artifact.txt", "generated"]);

        _ = await _processRunner.Received(1).RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(RemoveUntrackedPathsArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveUntrackedPaths_WithoutPaths_DoesNothing()
    {
        GitVersionControl sut = CreateSut();

        await sut.RemoveUntrackedPathsAsync("/repo", []);

        _ = await _processRunner.DidNotReceive().RunAsync(
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string?>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveUntrackedPaths_Failure_ThrowsInvalidOperation()
    {
        SetupProcessRunner(success: false, output: "fatal: clean failed");
        GitVersionControl sut = CreateSut();

        Func<Task> act = () => sut.RemoveUntrackedPathsAsync("/repo", ["artifact.txt"]);

        _ = (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*clean failed*");
    }

    [Fact]
    public async Task RevertLastCommit_CallsGitResetSoft()
    {
        SetupProcessRunner(success: true);
        GitVersionControl sut = CreateSut();

        await sut.RevertLastCommitAsync("/repo");

        _ = await _processRunner.Received(1).RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(ResetSoftArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }
}
