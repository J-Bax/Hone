using FluentAssertions;
using Hone.Core.Contracts;
using Hone.SourceControl.PullRequests;
using Hone.TestInfrastructure;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Hone.SourceControl.Tests.PullRequests;

public sealed class PullRequestManagerTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private readonly ICodeHost _codeHost = Substitute.For<ICodeHost>();

    private PullRequestManager CreateSut() => new(_codeHost);

    private static CreateExperimentPrOptions CreateOptions(
        int experiment = 1,
        string outcome = "improved",
        string description = "optimize loop",
        bool isDryRun = false,
        string baseBranch = "main",
        string branchName = "hone/experiment-1",
        string body = "PR body")
    {
        return new CreateExperimentPrOptions(
            Experiment: experiment,
            BranchName: branchName,
            BaseBranch: baseBranch,
            Outcome: outcome,
            Description: description,
            Body: body,
            IsDryRun: isDryRun);
    }

    private void SetupCodeHost()
    {
        _ = _codeHost.CreatePullRequestAsync(Arg.Any<CreatePrOptions>(), Arg.Any<CancellationToken>())
            .Returns(new PullRequestResult(Success: true, PrNumber: 42, PrUrl: new Uri("https://github.com/org/repo/pull/42")));
    }

    [Fact]
    public async Task CreatePR_SuccessfulExperiment_TitleContainsACCEPTED()
    {
        SetupCodeHost();
        CreateExperimentPrOptions options = CreateOptions(outcome: "improved");
        PullRequestManager sut = CreateSut();

        _ = await sut.CreateExperimentPrAsync(options);

        _ = await _codeHost.Received(1).CreatePullRequestAsync(
            Arg.Is<CreatePrOptions>(o => o.Title.Contains("[ACCEPTED]")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatePR_RejectedExperiment_TitleContainsREJECTED()
    {
        SetupCodeHost();
        CreateExperimentPrOptions options = CreateOptions(outcome: "regressed");
        PullRequestManager sut = CreateSut();

        _ = await sut.CreateExperimentPrAsync(options);

        _ = await _codeHost.Received(1).CreatePullRequestAsync(
            Arg.Is<CreatePrOptions>(o => o.Title.Contains("[REJECTED]")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatePR_DryRun_PrefixAddedToTitle()
    {
        SetupCodeHost();
        CreateExperimentPrOptions options = CreateOptions(isDryRun: true);
        PullRequestManager sut = CreateSut();

        _ = await sut.CreateExperimentPrAsync(options);

        _ = await _codeHost.Received(1).CreatePullRequestAsync(
            Arg.Is<CreatePrOptions>(o => o.Title.StartsWith("[DRY RUN] ")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatePR_Delegates_ToCodeHost()
    {
        SetupCodeHost();
        CreateExperimentPrOptions options = CreateOptions(
            baseBranch: "main",
            branchName: "hone/experiment-7",
            body: "## Experiment body");
        PullRequestManager sut = CreateSut();

        PullRequestResult result = await sut.CreateExperimentPrAsync(options);

        _ = result.Success.Should().BeTrue();
        _ = result.PrNumber.Should().Be(42);
        _ = await _codeHost.Received(1).CreatePullRequestAsync(
            Arg.Is<CreatePrOptions>(o =>
                o.BaseBranch == "main" &&
                o.HeadBranch == "hone/experiment-7" &&
                o.Body == "## Experiment body"),
            Arg.Any<CancellationToken>());
    }
}
