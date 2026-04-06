using FluentAssertions;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.TestInfrastructure;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Hone.SourceControl.Git.Tests;

public sealed class GitHubCodeHostTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private static readonly string[] PushFeatureArgs = ["push", "-u", "origin", "feature"];
    private static readonly string[] PrCreateArgs =
    [
        "pr", "create",
        "--base", "main",
        "--head", "feature",
        "--title", "My PR",
        "--body", "Description",
    ];
    private static readonly string[] PrView42Args = ["pr", "view", "42", "--json", "state,mergedAt"];

    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();

    private GitHubCodeHost CreateSut() => new(_processRunner);

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
    public async Task PushBranch_CallsGitPush_ReturnsSuccess()
    {
        SetupProcessRunner(success: true, output: "Everything up-to-date");
        GitHubCodeHost sut = CreateSut();

        PushResult result = await sut.PushBranchAsync("/repo", "feature");

        _ = result.Success.Should().BeTrue();
        _ = result.Output.Should().Be("Everything up-to-date");
        _ = await _processRunner.Received(1).RunAsync(
            "git",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(PushFeatureArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PushBranch_Failure_ReturnsError()
    {
        SetupProcessRunner(success: false, output: "push failed");
        GitHubCodeHost sut = CreateSut();

        PushResult result = await sut.PushBranchAsync("/repo", "feature");

        _ = result.Success.Should().BeFalse();
        _ = result.Output.Should().Be("push failed");
    }

    [Fact]
    public async Task CreatePR_Success_ParsesUrlAndNumber()
    {
        SetupProcessRunner(success: true, output: "https://github.com/owner/repo/pull/42\n");
        GitHubCodeHost sut = CreateSut();

        var options = new CreatePrOptions(
            BaseBranch: "main",
            HeadBranch: "feature",
            Title: "My PR",
            Body: "Description",
            WorkingDirectory: "/repo");

        PullRequestResult result = await sut.CreatePullRequestAsync(options);

        _ = result.Success.Should().BeTrue();
        _ = result.PrNumber.Should().Be(42);
        _ = result.PrUrl.Should().Be(new Uri("https://github.com/owner/repo/pull/42"));
        _ = await _processRunner.Received(1).RunAsync(
            "gh",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(PrCreateArgs)),
            "/repo",
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatePR_Failure_ReturnsError()
    {
        SetupProcessRunner(success: false, output: "gh: error");
        GitHubCodeHost sut = CreateSut();

        var options = new CreatePrOptions(
            BaseBranch: "main",
            HeadBranch: "feature",
            Title: "title",
            Body: "body",
            WorkingDirectory: "/repo");

        PullRequestResult result = await sut.CreatePullRequestAsync(options);

        _ = result.Success.Should().BeFalse();
        _ = result.PrNumber.Should().BeNull();
        _ = result.PrUrl.Should().BeNull();
    }

    [Fact]
    public async Task CreatePR_InvalidOutput_ReturnsError()
    {
        SetupProcessRunner(success: true, output: "not-a-url");
        GitHubCodeHost sut = CreateSut();

        var options = new CreatePrOptions(
            BaseBranch: "main",
            HeadBranch: "feature",
            Title: "title",
            Body: "body",
            WorkingDirectory: "/repo");

        PullRequestResult result = await sut.CreatePullRequestAsync(options);

        _ = result.Success.Should().BeFalse();
        _ = result.PrNumber.Should().BeNull();
        _ = result.PrUrl.Should().BeNull();
    }

    [Fact]
    public async Task GetPRStatus_ParsesJsonResponse_Merged()
    {
        SetupProcessRunner(success: true, output: """{"state":"MERGED","mergedAt":"2024-01-01T00:00:00Z"}""");
        GitHubCodeHost sut = CreateSut();

        PullRequestStatus result = await sut.GetPullRequestStatusAsync(prNumber: 42);

        _ = result.PrNumber.Should().Be(42);
        _ = result.State.Should().Be("MERGED");
        _ = result.Merged.Should().BeTrue();
        _ = await _processRunner.Received(1).RunAsync(
            "gh",
            Arg.Is<IEnumerable<string>>(args => args.SequenceEqual(PrView42Args)),
            workingDirectory: null,
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPRStatus_Open_MergedIsFalse()
    {
        SetupProcessRunner(success: true, output: """{"state":"OPEN","mergedAt":null}""");
        GitHubCodeHost sut = CreateSut();

        PullRequestStatus result = await sut.GetPullRequestStatusAsync(prNumber: 7);

        _ = result.PrNumber.Should().Be(7);
        _ = result.State.Should().Be("OPEN");
        _ = result.Merged.Should().BeFalse();
    }

    [Fact]
    public async Task GetPRStatus_Failure_ThrowsInvalidOperation()
    {
        SetupProcessRunner(success: false, output: "not found");
        GitHubCodeHost sut = CreateSut();

        Func<Task> act = () => sut.GetPullRequestStatusAsync(prNumber: 99);

        _ = (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*99*");
    }
}
