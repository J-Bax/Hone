using FluentAssertions;
using Hone.SourceControl.PullRequests;
using Xunit;

namespace Hone.SourceControl.Tests.PullRequests;

public sealed class StackNoteBuilderTests
{
    [Fact]
    public void BuildStackNote_EmptyChain_ReturnsEmpty()
    {
        var options = new StackNoteOptions(
            PrChain: [],
            FailedExperiments: [],
            Experiment: 1,
            OutcomeTag: "ACCEPTED",
            BaseBranch: "main");

        string result = StackNoteBuilder.Build(options);

        _ = result.Should().BeEmpty();
    }

    [Fact]
    public void BuildStackNote_SinglePR_FormatsCorrectly()
    {
        var options = new StackNoteOptions(
            PrChain: [new PrChainEntry(Experiment: 1, Number: 10, Outcome: "improved")],
            FailedExperiments: [],
            Experiment: 2,
            OutcomeTag: "ACCEPTED",
            BaseBranch: "main");

        string result = StackNoteBuilder.Build(options);

        _ = result.Should().Contain("**Stack:** `main`");
        _ = result.Should().Contain("PR #10 (experiment-1) ✓");
        _ = result.Should().Contain("**this PR** (experiment-2) ACCEPTED");
        _ = result.Should().Contain("**Base:** `main`");
    }

    [Fact]
    public void BuildStackNote_MultiPrChain_FormatsCorrectly()
    {
        var options = new StackNoteOptions(
            PrChain:
            [
                new PrChainEntry(Experiment: 1, Number: 10, Outcome: "improved"),
                new PrChainEntry(Experiment: 3, Number: 15, Outcome: "regressed"),
            ],
            FailedExperiments: [],
            Experiment: 5,
            OutcomeTag: "REJECTED",
            BaseBranch: "develop");

        string result = StackNoteBuilder.Build(options);

        _ = result.Should().Contain("**Stack:** `develop`");
        _ = result.Should().Contain("PR #10 (experiment-1) ✓");
        _ = result.Should().Contain("PR #15 (experiment-3) ✗");
        _ = result.Should().Contain("**this PR** (experiment-5) REJECTED");
        _ = result.Should().Contain("**Base:** `develop`");
    }

    [Fact]
    public void BuildStackNote_FailedExperimentsBetween_AddedToNote()
    {
        var options = new StackNoteOptions(
            PrChain: [new PrChainEntry(Experiment: 1, Number: 10, Outcome: "improved")],
            FailedExperiments:
            [
                new FailedExperimentEntry(Experiment: 2, Reason: "build_failure"),
                new FailedExperimentEntry(Experiment: 3, Reason: "regressed"),
            ],
            Experiment: 5,
            OutcomeTag: "ACCEPTED",
            BaseBranch: "main");

        string result = StackNoteBuilder.Build(options);

        _ = result.Should().Contain("2 (build_failure)");
        _ = result.Should().Contain("3 (regressed)");
        _ = result.Should().Contain("were attempted but did not produce branches");
    }

    [Fact]
    public void BuildStackNote_FailedExperimentsOutsideRange_NotIncluded()
    {
        var options = new StackNoteOptions(
            PrChain: [new PrChainEntry(Experiment: 3, Number: 10, Outcome: "improved")],
            FailedExperiments:
            [
                // Before the last chain entry — should be excluded
                new FailedExperimentEntry(Experiment: 1, Reason: "too_early"),
                // Equal to current experiment — should be excluded
                new FailedExperimentEntry(Experiment: 5, Reason: "same_as_current"),
                // After current experiment — should be excluded
                new FailedExperimentEntry(Experiment: 7, Reason: "too_late"),
            ],
            Experiment: 5,
            OutcomeTag: "ACCEPTED",
            BaseBranch: "main");

        string result = StackNoteBuilder.Build(options);

        _ = result.Should().NotContain("too_early");
        _ = result.Should().NotContain("same_as_current");
        _ = result.Should().NotContain("too_late");
        _ = result.Should().NotContain("were attempted but did not produce branches");
    }
}
