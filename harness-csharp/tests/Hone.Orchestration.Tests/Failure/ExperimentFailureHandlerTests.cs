using System.Text;
using FluentAssertions;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Orchestration.Failure;
using Hone.Orchestration.Queue;
using Hone.TestInfrastructure;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Orchestration.Tests.Failure;

public sealed class ExperimentFailureHandlerTests(ITestOutputHelper output) : HoneTestBase(output)
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static readonly List<Opportunity> SeedOpportunities =
    [
        new("src/Service1.cs", "Optimise Service1", "Explanation 1",
            OpportunityScope.Narrow, RootCause: null, ImpactEstimate: null),
    ];

    private (ExperimentFailureHandler Handler, IVersionControl Vc, IHoneEventSink Sink, OptimizationQueueManager Queue, string MetadataDir)
        CreateHandler(string name)
    {
        string metadataDir = CreateTargetDir(name);
        IVersionControl vc = Substitute.For<IVersionControl>();
        IHoneEventSink sink = Substitute.For<IHoneEventSink>();
        var queue = new OptimizationQueueManager(metadataDir, sink);
        var handler = new ExperimentFailureHandler(vc, queue, sink);
        return (handler, vc, sink, queue, metadataDir);
    }

    private static FailureContext DefaultContext(
        string? queueItemId = "1",
        bool skipMetadata = false,
        bool skipQueue = false,
        string targetDir = "C:\\target") =>
        new(
            BranchName: "hone/experiment-1",
            FilePath: "src/Service1.cs",
            Experiment: 1,
            Outcome: "regressed",
            RevertDescription: "Revert experiment 1 (regressed)",
            TargetDir: targetDir,
            MetadataSummary: "Tried optimisation of Service1",
            MetadataFilePath: "src/Service1.cs",
            QueueItemId: queueItemId,
            SkipMetadataUpdate: skipMetadata,
            SkipQueueMarkDone: skipQueue);

    // ── 1. Revert tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task HandleFailure_RevertsCode()
    {
        // Arrange
        (ExperimentFailureHandler handler, IVersionControl vc, _, _, _) =
            CreateHandler("revert-code");

        FailureContext ctx = DefaultContext(queueItemId: null, skipQueue: true);

        // Act
        FailureHandlerResult result = await handler.HandleFailureAsync(ctx);

        // Assert
        _ = result.RevertSucceeded.Should().BeTrue();
        _ = result.Success.Should().BeTrue();
        await vc.Received(1).RevertLastCommitAsync(ctx.TargetDir, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleFailure_RevertFails_ReturnsFalse()
    {
        // Arrange
        (ExperimentFailureHandler handler, IVersionControl vc, _, _, _) =
            CreateHandler("revert-fails-false");

        _ = vc.RevertLastCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .ThrowsAsync(new InvalidOperationException("git revert failed"));

        FailureContext ctx = DefaultContext(queueItemId: null, skipQueue: true);

        // Act
        FailureHandlerResult result = await handler.HandleFailureAsync(ctx);

        // Assert
        _ = result.RevertSucceeded.Should().BeFalse();
        _ = result.Success.Should().BeFalse("Success mirrors the revert result");
    }

    [Fact]
    public async Task HandleFailure_RevertFails_StillMarksQueue()
    {
        // Arrange
        (ExperimentFailureHandler handler, IVersionControl vc, _, OptimizationQueueManager queue, string metadataDir) =
            CreateHandler("revert-fails-queue");

        _ = queue.Initialize(SeedOpportunities, 0);
        _ = queue.GetNext(1);

        _ = vc.RevertLastCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .ThrowsAsync(new InvalidOperationException("git revert failed"));

        FailureContext ctx = DefaultContext(skipMetadata: true);

        // Act
        FailureHandlerResult result = await handler.HandleFailureAsync(ctx);

        // Assert — revert failed but queue was still marked
        _ = result.RevertSucceeded.Should().BeFalse();
        _ = result.QueueMarked.Should().BeTrue();

        // Verify queue file on disk reflects the mark
        string json = await File.ReadAllTextAsync(Path.Combine(metadataDir, "experiment-queue.json"), Encoding.UTF8);
        _ = json.Should().Contain("\"outcome\": \"regressed\"");
    }

    // ── 2. Queue tests ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleFailure_UpdatesQueueOutcome()
    {
        // Arrange
        (ExperimentFailureHandler handler, _, _, OptimizationQueueManager queue, string metadataDir) =
            CreateHandler("queue-outcome");

        _ = queue.Initialize(SeedOpportunities, 0);
        _ = queue.GetNext(1);

        FailureContext ctx = DefaultContext(skipMetadata: true);

        // Act
        FailureHandlerResult result = await handler.HandleFailureAsync(ctx);

        // Assert
        _ = result.QueueMarked.Should().BeTrue();

        string json = await File.ReadAllTextAsync(Path.Combine(metadataDir, "experiment-queue.json"), Encoding.UTF8);
        _ = json.Should().Contain("\"status\": \"done\"");
        _ = json.Should().Contain("\"outcome\": \"regressed\"");
        _ = json.Should().Contain("\"triedByExperiment\": 1");
    }

    [Fact]
    public async Task HandleFailure_SkipQueueMark_DoesNotMarkQueue()
    {
        // Arrange
        (ExperimentFailureHandler handler, _, _, OptimizationQueueManager queue, string metadataDir) =
            CreateHandler("skip-queue");

        _ = queue.Initialize(SeedOpportunities, 0);
        _ = queue.GetNext(1);

        FailureContext ctx = DefaultContext(skipQueue: true, skipMetadata: true);

        // Act
        FailureHandlerResult result = await handler.HandleFailureAsync(ctx);

        // Assert
        _ = result.QueueMarked.Should().BeFalse();

        // Queue should still show in_progress, not done
        string json = await File.ReadAllTextAsync(Path.Combine(metadataDir, "experiment-queue.json"), Encoding.UTF8);
        _ = json.Should().Contain("\"status\": \"in_progress\"");
    }

    [Fact]
    public async Task HandleFailure_NullQueueItemId_DoesNotMarkQueue()
    {
        // Arrange
        (ExperimentFailureHandler handler, _, _, _, _) =
            CreateHandler("null-queue-id");

        FailureContext ctx = DefaultContext(queueItemId: null, skipMetadata: true);

        // Act
        FailureHandlerResult result = await handler.HandleFailureAsync(ctx);

        // Assert
        _ = result.QueueMarked.Should().BeFalse();
    }

    // ── 3. Metadata callback tests ──────────────────────────────────────────

    [Fact]
    public async Task HandleFailure_RecordsMetadata()
    {
        // Arrange
        (ExperimentFailureHandler handler, _, _, _, _) =
            CreateHandler("metadata-callback");

        FailureContext? captured = null;

        FailureContext ctx = DefaultContext(queueItemId: null, skipQueue: true);

        // Act
        FailureHandlerResult result = await handler.HandleFailureAsync(ctx, onMetadataUpdate: RecordMetadata);

        // Assert
        _ = result.MetadataUpdated.Should().BeTrue();
        _ = captured.Should().NotBeNull();
        _ = captured!.Experiment.Should().Be(1);
        _ = captured.Outcome.Should().Be("regressed");
        _ = captured.MetadataSummary.Should().Be("Tried optimisation of Service1");

        Task RecordMetadata(FailureContext fc)
        {
            captured = fc;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task HandleFailure_SkipMetadata_DoesNotCallback()
    {
        // Arrange
        (ExperimentFailureHandler handler, _, _, _, _) =
            CreateHandler("skip-metadata");

        bool callbackInvoked = false;

        FailureContext ctx = DefaultContext(
            queueItemId: null,
            skipMetadata: true,
            skipQueue: true);

        // Act
        FailureHandlerResult result = await handler.HandleFailureAsync(ctx, onMetadataUpdate: MarkInvoked);

        // Assert
        _ = result.MetadataUpdated.Should().BeFalse();
        _ = callbackInvoked.Should().BeFalse();

        Task MarkInvoked(FailureContext _)
        {
            callbackInvoked = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task HandleFailure_NoCallback_MetadataNotUpdated()
    {
        // Arrange
        (ExperimentFailureHandler handler, _, _, _, _) =
            CreateHandler("no-callback");

        FailureContext ctx = DefaultContext(
            queueItemId: null,
            skipQueue: true);

        // Act — no callback supplied (null)
        FailureHandlerResult result = await handler.HandleFailureAsync(ctx);

        // Assert
        _ = result.MetadataUpdated.Should().BeFalse();
    }

    // ── 4. Artifact preservation test ───────────────────────────────────────

    [Fact]
    public async Task HandleFailure_PreservesArtifacts()
    {
        // The revert only undoes the last commit — experiment artifacts
        // (analysis files, logs, metrics) must remain on disk.

        // Arrange
        (ExperimentFailureHandler handler, IVersionControl vc, _, _, _) =
            CreateHandler("preserve-artifacts");

        string artifactDir = CreateTargetDir("preserve-artifacts-exp1", b =>
            _ = b.AddFile("analysis-prompt.md", "# Analysis prompt")
                 .AddFile("analysis-response.json", "{}")
                 .AddFile("build.log", "Build output"));

        FailureContext ctx = DefaultContext(
            queueItemId: null,
            skipMetadata: true,
            skipQueue: true,
            targetDir: artifactDir);

        // Act
        _ = await handler.HandleFailureAsync(ctx);

        // Assert — RevertLastCommitAsync was called (it only undoes the commit,
        // not the experiment directory), and artifact files are untouched.
        await vc.Received(1).RevertLastCommitAsync(artifactDir, Arg.Any<CancellationToken>());

        _ = File.Exists(Path.Combine(artifactDir, "analysis-prompt.md")).Should().BeTrue();
        _ = File.Exists(Path.Combine(artifactDir, "analysis-response.json")).Should().BeTrue();
        _ = File.Exists(Path.Combine(artifactDir, "build.log")).Should().BeTrue();
    }

    // ── 5. Event emission tests ─────────────────────────────────────────────

    [Fact]
    public async Task HandleFailure_EmitsCompletionEvent()
    {
        // Arrange
        (ExperimentFailureHandler handler, _, IHoneEventSink sink, _, _) =
            CreateHandler("emit-event");

        FailureContext ctx = DefaultContext(
            queueItemId: null,
            skipMetadata: true,
            skipQueue: true);

        // Act
        _ = await handler.HandleFailureAsync(ctx);

        // Assert — at least one StatusMessage with the completion summary
        sink.Received().Emit(Arg.Is<StatusMessage>(e =>
            e.Message.Contains("Failure handler completed", StringComparison.Ordinal) &&
            e.Experiment == 1));
    }

    [Fact]
    public async Task HandleFailure_RevertFails_EmitsWarning()
    {
        // Arrange
        (ExperimentFailureHandler handler, IVersionControl vc, IHoneEventSink sink, _, _) =
            CreateHandler("emit-warning");

        _ = vc.RevertLastCommitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .ThrowsAsync(new InvalidOperationException("merge conflict"));

        FailureContext ctx = DefaultContext(
            queueItemId: null,
            skipMetadata: true,
            skipQueue: true);

        // Act
        _ = await handler.HandleFailureAsync(ctx);

        // Assert — a Warning-level event should describe the revert failure
        sink.Received().Emit(Arg.Is<StatusMessage>(e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("Revert failed", StringComparison.Ordinal)));
    }

    // ── 6. Full integration (all three steps) ───────────────────────────────

    [Fact]
    public async Task HandleFailure_AllSteps_Succeed()
    {
        // Arrange
        (ExperimentFailureHandler handler, _, _, OptimizationQueueManager queue, string metadataDir) =
            CreateHandler("all-steps");

        _ = queue.Initialize(SeedOpportunities, 0);
        _ = queue.GetNext(1);

        bool metadataRecorded = false;

        FailureContext ctx = DefaultContext();

        // Act
        FailureHandlerResult result = await handler.HandleFailureAsync(ctx, onMetadataUpdate: RecordMetadata);

        // Assert
        _ = result.Success.Should().BeTrue();
        _ = result.RevertSucceeded.Should().BeTrue();
        _ = result.MetadataUpdated.Should().BeTrue();
        _ = result.QueueMarked.Should().BeTrue();
        _ = metadataRecorded.Should().BeTrue();

        // Verify queue state on disk
        string json = await File.ReadAllTextAsync(Path.Combine(metadataDir, "experiment-queue.json"), Encoding.UTF8);
        _ = json.Should().Contain("\"status\": \"done\"");

        Task RecordMetadata(FailureContext _)
        {
            metadataRecorded = true;
            return Task.CompletedTask;
        }
    }
}
