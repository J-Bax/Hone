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
    private static readonly string[] CheckoutFeatureArgs = ["checkout", "feature"];
    private static readonly string[] CheckoutCreateFeatureArgs = ["checkout", "-b", "feature"];
    private static readonly string[] AddFilesArgs = ["add", "file1.cs", "file2.cs"];
    private static readonly string[] CommitArgs = ["commit", "--no-gpg-sign", "-m", "fix bug"];
    private static readonly string[] DiffArgs = ["diff"];
    private static readonly string[] DiffThreeDotArgs = ["diff", "main...HEAD"];
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
