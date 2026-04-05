using System.Text.Json;
using FluentAssertions;
using Hone.Core.Config;
using Hone.Core.Observability;
using Hone.Orchestration.Implementer;
using Hone.TestInfrastructure;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Orchestration.Tests.Implementer;

public sealed class IterativeImplementerRunnerTests(ITestOutputHelper output)
    : HoneTestBase(output)
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ImplementerOptions MakeOptions(
        string targetDir,
        string filePath = "src/Service.cs",
        int experiment = 1,
        int maxAttempts = 1,
        double maxDiffGrowthFactor = 0,
        bool testFileGuard = false,
        IReadOnlyList<string>? testProjectPaths = null,
        string resultsPath = "results",
        string branchPrefix = "hone/opt")
    {
        return new ImplementerOptions(
            FilePath: filePath,
            Explanation: "Optimise hot path in Service",
            RootCauseDocument: null,
            Experiment: experiment,
            BaseBranch: "main",
            TargetDir: targetDir,
            TargetName: null,
            Config: new ImplementerConfig(maxAttempts, maxDiffGrowthFactor, testFileGuard),
            TestProjectPaths: testProjectPaths,
            BranchPrefix: branchPrefix,
            ResultsPath: resultsPath);
    }

    private static FixStepResult OkFix(string codeBlock = "// fixed code") =>
        new(Success: true, CodeBlock: codeBlock,
            PromptPath: null, ResponsePath: null,
            AttemptPromptPath: null, AttemptResponsePath: null);

    private static ApplyStepResult OkApply(string sha = "abc123") =>
        new(Success: true, CommitSha: sha, Description: null);

    private static BuildStepResult OkBuild() =>
        new(Success: true, Output: null);

    private static BuildStepResult FailBuild(string output = "CS0001: compile error") =>
        new(Success: false, Output: output);

    private static TestStepResult OkTest() =>
        new(Success: true, Output: null);

    private static TestStepResult FailTest(string output = "Assert.Equal failed") =>
        new(Success: false, Output: output);

    // ── 1. SingleAttempt_BuildPasses_Success ────────────────────────────────

    [Fact]
    public async Task SingleAttempt_BuildPasses_Success()
    {
        // Arrange
        string targetDir = CreateTargetDir("single-ok", b =>
            b.AddFile("src/Service.cs", "// original code"));

        IImplementerPipeline pipeline = Substitute.For<IImplementerPipeline>();
        IHoneEventSink sink = Substitute.For<IHoneEventSink>();

        _ = pipeline.InvokeFixAgentAsync(Arg.Any<FixStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkFix()));
        _ = pipeline.ApplySuggestionAsync(Arg.Any<ApplyStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkApply()));
        _ = pipeline.GetDiffLineCountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(10));
        _ = pipeline.BuildProjectAsync(Arg.Any<BuildStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkBuild()));
        _ = pipeline.RunTestsAsync(Arg.Any<TestStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkTest()));

        ImplementerOptions options = MakeOptions(targetDir);
        var runner = new IterativeImplementerRunner(pipeline, sink);

        // Act
        ImplementerRunResult result = await runner.RunAsync(options);

        // Assert
        _ = result.Result.Success.Should().BeTrue();
        _ = result.Result.AttemptCount.Should().Be(1);
        _ = result.Result.ExitReason.Should().Be("success");
        _ = result.BranchName.Should().Be("hone/opt-1");
        _ = result.CommitSha.Should().Be("abc123");
    }

    // ── 2. BuildFailure_RetriesWithErrors ───────────────────────────────────

    [Fact]
    public async Task BuildFailure_RetriesWithErrors()
    {
        // Arrange
        string targetDir = CreateTargetDir("build-retry", b =>
            b.AddFile("src/Service.cs", "// original code"));

        IImplementerPipeline pipeline = Substitute.For<IImplementerPipeline>();
        IHoneEventSink sink = Substitute.For<IHoneEventSink>();

        _ = pipeline.InvokeFixAgentAsync(Arg.Any<FixStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkFix()));
        _ = pipeline.ApplySuggestionAsync(Arg.Any<ApplyStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkApply()));
        _ = pipeline.GetDiffLineCountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(5));

        // Build: fails first, then succeeds
        _ = pipeline.BuildProjectAsync(Arg.Any<BuildStepInput>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(FailBuild("CS0001: type not found")),
                Task.FromResult(OkBuild()));

        _ = pipeline.RunTestsAsync(Arg.Any<TestStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkTest()));

        ImplementerOptions options = MakeOptions(targetDir, maxAttempts: 3);
        var runner = new IterativeImplementerRunner(pipeline, sink);

        // Act
        ImplementerRunResult result = await runner.RunAsync(options);

        // Assert
        _ = result.Result.Success.Should().BeTrue();
        _ = result.Result.AttemptCount.Should().Be(2);
        _ = result.Result.ExitReason.Should().Be("success");

        // Verify revert was called once (after first failure)
        await pipeline.Received(1).RevertForRetryAsync(
            Arg.Any<RevertInput>(), Arg.Any<CancellationToken>());

        // Verify second fix call received previous errors
        IReadOnlyList<FixStepInput> fixCalls =
            [.. pipeline.ReceivedCalls()
                .Where(c => string.Equals(c.GetMethodInfo().Name, nameof(IImplementerPipeline.InvokeFixAgentAsync), StringComparison.Ordinal))
                .Select(c => (FixStepInput)c.GetArguments()[0]!),];

        _ = fixCalls.Should().HaveCount(2);
        _ = fixCalls[0].PreviousErrors.Should().BeNull();
        _ = fixCalls[1].PreviousErrors.Should().NotBeNull()
            .And.Contain("build");
    }

    // ── 3. TestFailure_RetriesWithOutput ────────────────────────────────────

    [Fact]
    public async Task TestFailure_RetriesWithOutput()
    {
        // Arrange
        string targetDir = CreateTargetDir("test-retry", b =>
            b.AddFile("src/Service.cs", "// original code"));

        IImplementerPipeline pipeline = Substitute.For<IImplementerPipeline>();
        IHoneEventSink sink = Substitute.For<IHoneEventSink>();

        _ = pipeline.InvokeFixAgentAsync(Arg.Any<FixStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkFix()));
        _ = pipeline.ApplySuggestionAsync(Arg.Any<ApplyStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkApply()));
        _ = pipeline.GetDiffLineCountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(8));
        _ = pipeline.BuildProjectAsync(Arg.Any<BuildStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkBuild()));

        // Tests: fail first, then pass
        _ = pipeline.RunTestsAsync(Arg.Any<TestStepInput>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(FailTest("Expected 200 but got 500")),
                Task.FromResult(OkTest()));

        ImplementerOptions options = MakeOptions(targetDir, maxAttempts: 3);
        var runner = new IterativeImplementerRunner(pipeline, sink);

        // Act
        ImplementerRunResult result = await runner.RunAsync(options);

        // Assert
        _ = result.Result.Success.Should().BeTrue();
        _ = result.Result.AttemptCount.Should().Be(2);

        await pipeline.Received(1).RevertForRetryAsync(
            Arg.Any<RevertInput>(), Arg.Any<CancellationToken>());

        // Verify second fix call received test error context
        IReadOnlyList<FixStepInput> fixCalls =
            [.. pipeline.ReceivedCalls()
                .Where(c => string.Equals(c.GetMethodInfo().Name, nameof(IImplementerPipeline.InvokeFixAgentAsync), StringComparison.Ordinal))
                .Select(c => (FixStepInput)c.GetArguments()[0]!),];

        _ = fixCalls[1].PreviousErrors.Should().Contain("test");
        _ = fixCalls[1].CurrentFileContent.Should().Be("// original code");
    }

    // ── 4. MaxAttempts_Exhausted_ReturnsFailure ─────────────────────────────

    [Fact]
    public async Task MaxAttempts_Exhausted_ReturnsFailure()
    {
        // Arrange
        string targetDir = CreateTargetDir("exhausted", b =>
            b.AddFile("src/Service.cs", "// code"));

        IImplementerPipeline pipeline = Substitute.For<IImplementerPipeline>();
        IHoneEventSink sink = Substitute.For<IHoneEventSink>();

        _ = pipeline.InvokeFixAgentAsync(Arg.Any<FixStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkFix()));
        _ = pipeline.ApplySuggestionAsync(Arg.Any<ApplyStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkApply()));
        _ = pipeline.GetDiffLineCountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(5));

        // Build always fails
        _ = pipeline.BuildProjectAsync(Arg.Any<BuildStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(FailBuild()));

        ImplementerOptions options = MakeOptions(targetDir, maxAttempts: 2);
        var runner = new IterativeImplementerRunner(pipeline, sink);

        // Act
        ImplementerRunResult result = await runner.RunAsync(options);

        // Assert
        _ = result.Result.Success.Should().BeFalse();
        _ = result.Result.ExitReason.Should().Be("retry_budget_exhausted");
        _ = result.Result.AttemptCount.Should().Be(2);
        _ = result.Result.FailureDetail.Should().NotBeNullOrWhiteSpace();

        // Revert called once (after first failure; not after last)
        await pipeline.Received(1).RevertForRetryAsync(
            Arg.Any<RevertInput>(), Arg.Any<CancellationToken>());
    }

    // ── 5. DiffGrowth_Exceeded_RejectsIteration ─────────────────────────────

    [Fact]
    public async Task DiffGrowth_Exceeded_RejectsIteration()
    {
        // Arrange
        string targetDir = CreateTargetDir("diff-growth", b =>
            b.AddFile("src/Service.cs", "// code"));

        IImplementerPipeline pipeline = Substitute.For<IImplementerPipeline>();
        IHoneEventSink sink = Substitute.For<IHoneEventSink>();

        _ = pipeline.InvokeFixAgentAsync(Arg.Any<FixStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkFix()));
        _ = pipeline.ApplySuggestionAsync(Arg.Any<ApplyStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkApply()));

        // First attempt: 10 lines, build fails → retry
        // Second attempt: 25 lines (> 10 × 2.0 = 20) → guard rejects → budget exhausted
        int callCount = 0;
        _ = pipeline.GetDiffLineCountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                int current = Interlocked.Increment(ref callCount);
                return Task.FromResult(current == 1 ? 10 : 25);
            });

        _ = pipeline.BuildProjectAsync(Arg.Any<BuildStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(FailBuild()));

        ImplementerOptions options = MakeOptions(
            targetDir, maxAttempts: 2, maxDiffGrowthFactor: 2.0);
        var runner = new IterativeImplementerRunner(pipeline, sink);

        // Act
        ImplementerRunResult result = await runner.RunAsync(options);

        // Assert
        _ = result.Result.Success.Should().BeFalse();
        _ = result.Result.ExitReason.Should().Be("retry_budget_exhausted");
        _ = result.Result.FailureDetail.Should().Contain("Diff grew");

        // The iteration log should record a guard rejection on attempt 2
        _ = result.Result.IterationLog.Should().NotBeNull();
        _ = result.Result.IterationLog!.Attempts.Should().HaveCount(2);
        _ = result.Result.IterationLog.Attempts[1].Stage.Should().Be("guard");
        _ = result.Result.IterationLog.Attempts[1].Outcome.Should().Be("rejected");
    }

    // ── 6. TestFileGuard_BlocksTestModification ─────────────────────────────

    [Fact]
    public async Task TestFileGuard_BlocksTestModification()
    {
        // Arrange
        string targetDir = CreateTargetDir("test-guard", b =>
            b.AddFile("src/Service.cs", "// code"));

        IImplementerPipeline pipeline = Substitute.For<IImplementerPipeline>();
        IHoneEventSink sink = Substitute.For<IHoneEventSink>();

        _ = pipeline.InvokeFixAgentAsync(Arg.Any<FixStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkFix()));
        _ = pipeline.ApplySuggestionAsync(Arg.Any<ApplyStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkApply()));
        _ = pipeline.GetDiffLineCountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(5));

        // First attempt: changed files include a test → guard rejects
        // Second attempt: changed files clean → build + test pass
        int changedCallCount = 0;
        _ = pipeline.GetChangedFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                int current = Interlocked.Increment(ref changedCallCount);
                return current == 1
                    ? Task.FromResult<IReadOnlyList<string>>(
                        ["src/Service.cs", "tests/MyProject.Tests/ServiceTests.cs"])
                    : Task.FromResult<IReadOnlyList<string>>(["src/Service.cs"]);
            });

        _ = pipeline.BuildProjectAsync(Arg.Any<BuildStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkBuild()));
        _ = pipeline.RunTestsAsync(Arg.Any<TestStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkTest()));

        ImplementerOptions options = MakeOptions(
            targetDir, maxAttempts: 2, testFileGuard: true,
            testProjectPaths: ["tests/MyProject.Tests"]);
        var runner = new IterativeImplementerRunner(pipeline, sink);

        // Act
        ImplementerRunResult result = await runner.RunAsync(options);

        // Assert — second attempt succeeds after guard rejection
        _ = result.Result.Success.Should().BeTrue();
        _ = result.Result.AttemptCount.Should().Be(2);

        // First attempt was rejected by guard
        _ = result.Result.IterationLog.Should().NotBeNull();
        _ = result.Result.IterationLog!.Attempts[0].Stage.Should().Be("guard");
        _ = result.Result.IterationLog.Attempts[0].Outcome.Should().Be("rejected");

        // Revert was called
        await pipeline.Received(1).RevertForRetryAsync(
            Arg.Any<RevertInput>(), Arg.Any<CancellationToken>());
    }

    // ── 7. PerAttempt_ArtifactsPreserved ────────────────────────────────────

    [Fact]
    public async Task PerAttempt_ArtifactsPreserved()
    {
        // Arrange
        string targetDir = CreateTargetDir("artifacts", b =>
            b.AddFile("src/Service.cs", "// code"));

        IImplementerPipeline pipeline = Substitute.For<IImplementerPipeline>();
        IHoneEventSink sink = Substitute.For<IHoneEventSink>();

        _ = pipeline.InvokeFixAgentAsync(Arg.Any<FixStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkFix()));
        _ = pipeline.ApplySuggestionAsync(Arg.Any<ApplyStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkApply()));
        _ = pipeline.GetDiffLineCountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(5));

        // Build: fails then succeeds
        _ = pipeline.BuildProjectAsync(Arg.Any<BuildStepInput>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(FailBuild()),
                Task.FromResult(OkBuild()));
        _ = pipeline.RunTestsAsync(Arg.Any<TestStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkTest()));

        ImplementerOptions options = MakeOptions(targetDir, maxAttempts: 2);
        var runner = new IterativeImplementerRunner(pipeline, sink);

        // Act
        ImplementerRunResult result = await runner.RunAsync(options);

        // Assert — both attempt directories were created
        _ = result.Result.Success.Should().BeTrue();

        string attempt1Dir = Path.Combine(
            targetDir, "results", "experiment-1", "iterations", "attempt-1");
        string attempt2Dir = Path.Combine(
            targetDir, "results", "experiment-1", "iterations", "attempt-2");

        _ = Directory.Exists(attempt1Dir).Should().BeTrue(
            "attempt-1 directory should be created");
        _ = Directory.Exists(attempt2Dir).Should().BeTrue(
            "attempt-2 directory should be created");
    }

    // ── 8. IterationLog_RecordsAllAttempts ──────────────────────────────────

    [Fact]
    public async Task IterationLog_RecordsAllAttempts()
    {
        // Arrange
        string targetDir = CreateTargetDir("log-records", b =>
            b.AddFile("src/Service.cs", "// code"));

        IImplementerPipeline pipeline = Substitute.For<IImplementerPipeline>();
        IHoneEventSink sink = Substitute.For<IHoneEventSink>();

        _ = pipeline.InvokeFixAgentAsync(Arg.Any<FixStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkFix()));
        _ = pipeline.ApplySuggestionAsync(Arg.Any<ApplyStepInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(OkApply("sha-final")));
        _ = pipeline.GetDiffLineCountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(12));

        // Attempt 1: build fails
        // Attempt 2: build OK, test fails
        // Attempt 3: build OK, test OK
        int buildCallCount = 0;
        _ = pipeline.BuildProjectAsync(Arg.Any<BuildStepInput>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                int current = Interlocked.Increment(ref buildCallCount);
                return current == 1
                    ? Task.FromResult(FailBuild("link error"))
                    : Task.FromResult(OkBuild());
            });

        int testCallCount = 0;
        _ = pipeline.RunTestsAsync(Arg.Any<TestStepInput>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                int current = Interlocked.Increment(ref testCallCount);
                return current == 1
                    ? Task.FromResult(FailTest("timeout on test X"))
                    : Task.FromResult(OkTest());
            });

        ImplementerOptions options = MakeOptions(targetDir, maxAttempts: 3);
        var runner = new IterativeImplementerRunner(pipeline, sink);

        // Act
        ImplementerRunResult result = await runner.RunAsync(options);

        // Assert — success on third attempt
        _ = result.Result.Success.Should().BeTrue();
        _ = result.Result.AttemptCount.Should().Be(3);

        // Verify iteration-log.json on disk
        string logPath = Path.Combine(
            targetDir, "results", "experiment-1", "iteration-log.json");
        _ = File.Exists(logPath).Should().BeTrue("iteration-log.json must be written");

        string json = await File.ReadAllTextAsync(logPath);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        _ = root.GetProperty("experiment").GetInt32().Should().Be(1);
        _ = root.GetProperty("totalAttempts").GetInt32().Should().Be(3);
        _ = root.GetProperty("finalOutcome").GetString().Should().Be("success");

        JsonElement attempts = root.GetProperty("attempts");
        _ = attempts.GetArrayLength().Should().Be(3);

        // Attempt 1: build failed
        _ = attempts[0].GetProperty("attempt").GetInt32().Should().Be(1);
        _ = attempts[0].GetProperty("stage").GetString().Should().Be("build");
        _ = attempts[0].GetProperty("outcome").GetString().Should().Be("failed");

        // Attempt 2: test failed
        _ = attempts[1].GetProperty("attempt").GetInt32().Should().Be(2);
        _ = attempts[1].GetProperty("stage").GetString().Should().Be("test");
        _ = attempts[1].GetProperty("outcome").GetString().Should().Be("failed");

        // Attempt 3: test passed
        _ = attempts[2].GetProperty("attempt").GetInt32().Should().Be(3);
        _ = attempts[2].GetProperty("stage").GetString().Should().Be("test");
        _ = attempts[2].GetProperty("outcome").GetString().Should().Be("passed");
    }
}
