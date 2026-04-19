using System.Text;
using FluentAssertions;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Orchestration.Failure;
using Hone.Orchestration.Queue;
using Hone.Orchestration.State;
using Hone.TestInfrastructure;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Orchestration.Tests.Failure;

public sealed class ExperimentFailureHandlerTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private static readonly List<Opportunity> SeedOpportunities =
    [
        new("src/Service1.cs", "Optimise Service1", "Explanation 1",
            OpportunityScope.Narrow, RootCause: null, ImpactEstimate: null),
    ];

    private (ExperimentFailureHandler Handler, IVersionControl Vc, IHoneEventSink Sink, OptimizationQueueManager Queue, IRunStateStore RunStateStore, string TargetDir)
        CreateHandler(string name)
    {
        string targetDir = CreateTargetDir(name);
        IVersionControl vc = Substitute.For<IVersionControl>();
        IHoneEventSink sink = Substitute.For<IHoneEventSink>();
        var queue = new OptimizationQueueManager(targetDir, sink);
        IRunStateStore runStateStore = new RunStateStore(targetDir, ".");

        _ = vc.GetTouchedTrackedPathsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["src/Service1.cs"]));
        _ = vc.GetUntrackedPathsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));
        _ = vc.RestoreTrackedPathsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _ = vc.RemoveUntrackedPathsAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _ = vc.CheckoutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _ = vc.GetCurrentBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("main"));
        _ = vc.GetHeadShaAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("stable-sha"));
        _ = vc.IsWorkingTreeCleanAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var handler = new ExperimentFailureHandler(vc, runStateStore, queue, sink);
        return (handler, vc, sink, queue, runStateStore, targetDir);
    }

    private static FailureContext DefaultContext(
        string targetDir,
        string? queueItemId = "1",
        bool skipMetadata = false,
        bool skipQueue = false,
        string? cleanupManifestPath = null,
        IReadOnlyList<string>? knownUntrackedPaths = null,
        string? expectedStableHeadSha = "stable-sha") =>
        new(
            BranchName: "hone/experiment-1",
            BaseBranch: "main",
            FilePath: "src/Service1.cs",
            Experiment: 1,
            Outcome: "regressed",
            RevertDescription: "Revert experiment 1 (regressed)",
            TargetDir: targetDir,
            ExpectedStableHeadSha: expectedStableHeadSha,
            CleanupManifestPath: cleanupManifestPath,
            KnownUntrackedPaths: knownUntrackedPaths,
            MetadataSummary: "Tried optimisation of Service1",
            MetadataFilePath: "src/Service1.cs",
            QueueItemId: queueItemId,
            SkipMetadataUpdate: skipMetadata,
            SkipQueueMarkDone: skipQueue);

    [Fact]
    public async Task HandleFailure_DerivesManifestAndCleansStableBranch()
    {
        (ExperimentFailureHandler handler, IVersionControl vc, _, _, IRunStateStore runStateStore, string targetDir) =
            CreateHandler("derive-manifest");

        string manifestPath = runStateStore.GetCleanupManifestPath(1);
        Directory.CreateDirectory(Path.Combine(targetDir, "hone-results", "experiment-1"));
        await File.WriteAllTextAsync(Path.Combine(targetDir, "scratch.txt"), "scratch", Encoding.UTF8);

        _ = vc.GetHeadShaAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult("candidate-sha"),
                Task.FromResult("stable-sha"));
        _ = vc.GetTouchedTrackedPathsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["src/Service1.cs", "src/Helper.cs"]));
        _ = vc.GetUntrackedPathsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["scratch.txt"]));

        FailureContext ctx = DefaultContext(
            targetDir: targetDir,
            queueItemId: null,
            skipQueue: true,
            cleanupManifestPath: manifestPath,
            knownUntrackedPaths: ["hone-results/experiment-1"]);

        FailureHandlerResult result = await handler.HandleFailureAsync(ctx);
        CleanupManifest? manifest = await runStateStore.LoadCleanupManifestAsync(manifestPath);

        _ = result.Success.Should().BeTrue();
        _ = result.CleanupSucceeded.Should().BeTrue();
        _ = result.VerificationSucceeded.Should().BeTrue();
        _ = result.CleanupManifestPath.Should().Be(manifestPath);
        _ = result.TrackedPaths.Should().Equal("src/Helper.cs", "src/Service1.cs");
        _ = result.UntrackedPaths.Should().Equal("hone-results/experiment-1", "scratch.txt");
        _ = result.ObservedBranch.Should().Be("main");
        _ = result.ObservedHeadSha.Should().Be("stable-sha");
        _ = result.WorktreeCleanAfterCleanup.Should().BeTrue();

        _ = manifest.Should().NotBeNull();
        _ = manifest!.CandidateHeadSha.Should().Be("candidate-sha");
        _ = manifest.ExpectedStableHeadSha.Should().Be("stable-sha");
        _ = manifest.TrackedPaths.Should().Equal("src/Helper.cs", "src/Service1.cs");
        _ = manifest.UntrackedPaths.Should().Equal("hone-results/experiment-1", "scratch.txt");

        string[] expectedTrackedPaths = ["src/Helper.cs", "src/Service1.cs"];
        string[] expectedUntrackedPaths = ["hone-results/experiment-1", "scratch.txt"];
        await vc.Received(1).RestoreTrackedPathsAsync(
            targetDir,
            "main",
            Arg.Is<IEnumerable<string>>(paths => paths.SequenceEqual(expectedTrackedPaths)),
            Arg.Any<CancellationToken>());
        await vc.Received(1).RemoveUntrackedPathsAsync(
            targetDir,
            Arg.Is<IEnumerable<string>>(paths => paths.SequenceEqual(expectedUntrackedPaths)),
            Arg.Any<CancellationToken>());
        await vc.Received(1).CheckoutAsync(
            targetDir,
            "main",
            create: false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleFailure_CleanupFails_ReturnsFalse()
    {
        (ExperimentFailureHandler handler, IVersionControl vc, _, _, IRunStateStore runStateStore, string targetDir) =
            CreateHandler("cleanup-fails-false");

        string manifestPath = runStateStore.GetCleanupManifestPath(1);
        _ = vc.GetTouchedTrackedPathsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("git diff failed"));

        FailureContext ctx = DefaultContext(
            targetDir: targetDir,
            queueItemId: null,
            skipQueue: true,
            cleanupManifestPath: manifestPath);

        FailureHandlerResult result = await handler.HandleFailureAsync(ctx);

        _ = result.Success.Should().BeFalse();
        _ = result.CleanupSucceeded.Should().BeFalse();
        _ = result.VerificationSucceeded.Should().BeFalse();
        _ = result.CleanupManifestPath.Should().Be(manifestPath);
        _ = result.FailureMessage.Should().Contain("git diff failed");
    }

    [Fact]
    public async Task HandleFailure_CleanupFails_StillMarksQueue()
    {
        (ExperimentFailureHandler handler, IVersionControl vc, _, OptimizationQueueManager queue, _, string targetDir) =
            CreateHandler("cleanup-fails-queue");

        _ = queue.Initialize(SeedOpportunities, 0);
        _ = queue.GetNext(1);

        _ = vc.GetTouchedTrackedPathsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("git diff failed"));

        FailureContext ctx = DefaultContext(targetDir: targetDir, skipMetadata: true);

        FailureHandlerResult result = await handler.HandleFailureAsync(ctx);

        _ = result.CleanupSucceeded.Should().BeFalse();
        _ = result.QueueMarked.Should().BeTrue();

        string json = await File.ReadAllTextAsync(Path.Combine(targetDir, "experiment-queue.json"), Encoding.UTF8);
        _ = json.Should().Contain("\"outcome\": \"regressed\"");
    }

    [Fact]
    public async Task HandleFailure_UpdatesQueueOutcome()
    {
        (ExperimentFailureHandler handler, _, _, OptimizationQueueManager queue, _, string targetDir) =
            CreateHandler("queue-outcome");

        _ = queue.Initialize(SeedOpportunities, 0);
        _ = queue.GetNext(1);

        FailureContext ctx = DefaultContext(targetDir: targetDir, skipMetadata: true);

        FailureHandlerResult result = await handler.HandleFailureAsync(ctx);

        _ = result.QueueMarked.Should().BeTrue();

        string json = await File.ReadAllTextAsync(Path.Combine(targetDir, "experiment-queue.json"), Encoding.UTF8);
        _ = json.Should().Contain("\"status\": \"done\"");
        _ = json.Should().Contain("\"outcome\": \"regressed\"");
        _ = json.Should().Contain("\"triedByExperiment\": 1");
    }

    [Fact]
    public async Task HandleFailure_SkipQueueMark_DoesNotMarkQueue()
    {
        (ExperimentFailureHandler handler, _, _, OptimizationQueueManager queue, _, string targetDir) =
            CreateHandler("skip-queue");

        _ = queue.Initialize(SeedOpportunities, 0);
        _ = queue.GetNext(1);

        FailureContext ctx = DefaultContext(targetDir: targetDir, skipQueue: true, skipMetadata: true);

        FailureHandlerResult result = await handler.HandleFailureAsync(ctx);

        _ = result.QueueMarked.Should().BeFalse();

        string json = await File.ReadAllTextAsync(Path.Combine(targetDir, "experiment-queue.json"), Encoding.UTF8);
        _ = json.Should().Contain("\"status\": \"in_progress\"");
    }

    [Fact]
    public async Task HandleFailure_NullQueueItemId_DoesNotMarkQueue()
    {
        (ExperimentFailureHandler handler, _, _, _, _, string targetDir) =
            CreateHandler("null-queue-id");

        FailureContext ctx = DefaultContext(targetDir: targetDir, queueItemId: null, skipMetadata: true);

        FailureHandlerResult result = await handler.HandleFailureAsync(ctx);

        _ = result.QueueMarked.Should().BeFalse();
    }

    [Fact]
    public async Task HandleFailure_RecordsMetadata()
    {
        (ExperimentFailureHandler handler, _, _, _, _, string targetDir) =
            CreateHandler("metadata-callback");

        FailureContext? captured = null;
        FailureContext ctx = DefaultContext(targetDir: targetDir, queueItemId: null, skipQueue: true);

        FailureHandlerResult result = await handler.HandleFailureAsync(ctx, onMetadataUpdate: RecordMetadata);

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
        (ExperimentFailureHandler handler, _, _, _, _, string targetDir) =
            CreateHandler("skip-metadata");

        bool callbackInvoked = false;
        FailureContext ctx = DefaultContext(
            targetDir: targetDir,
            queueItemId: null,
            skipMetadata: true,
            skipQueue: true);

        FailureHandlerResult result = await handler.HandleFailureAsync(ctx, onMetadataUpdate: MarkInvoked);

        _ = result.MetadataUpdated.Should().BeFalse();
        _ = callbackInvoked.Should().BeFalse();

        Task MarkInvoked(FailureContext _)
        {
            callbackInvoked = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task HandleFailure_UsesPersistedManifestOnRetry()
    {
        (ExperimentFailureHandler handler, IVersionControl vc, _, _, IRunStateStore runStateStore, string targetDir) =
            CreateHandler("persisted-manifest");

        string manifestPath = runStateStore.GetCleanupManifestPath(1);
        Directory.CreateDirectory(Path.Combine(targetDir, "hone-results", "experiment-1"));
        await runStateStore.SaveCleanupManifestAsync(
            manifestPath,
            new CleanupManifest
            {
                Experiment = 1,
                BranchName = "hone/experiment-1",
                BaseBranch = "main",
                CandidateHeadSha = "candidate-sha",
                ExpectedStableHeadSha = "stable-sha",
                TrackedPaths = ["src/Service1.cs"],
                UntrackedPaths = ["hone-results/experiment-1"],
            });

        FailureContext ctx = DefaultContext(
            targetDir: targetDir,
            queueItemId: null,
            skipQueue: true,
            cleanupManifestPath: manifestPath);

        FailureHandlerResult result = await handler.HandleFailureAsync(ctx);

        _ = result.Success.Should().BeTrue();
        _ = result.TrackedPaths.Should().Equal("src/Service1.cs");
        _ = result.UntrackedPaths.Should().Equal("hone-results/experiment-1");

        _ = await vc.DidNotReceive().GetTouchedTrackedPathsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        _ = await vc.DidNotReceive().GetUntrackedPathsAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleFailure_WhenPersistedManifestPathsAlreadyRemoved_RemainsIdempotent()
    {
        (ExperimentFailureHandler handler, IVersionControl vc, _, _, IRunStateStore runStateStore, string targetDir) =
            CreateHandler("persisted-manifest-idempotent");

        string manifestPath = runStateStore.GetCleanupManifestPath(1);
        await runStateStore.SaveCleanupManifestAsync(
            manifestPath,
            new CleanupManifest
            {
                Experiment = 1,
                BranchName = "hone/experiment-1",
                BaseBranch = "main",
                CandidateHeadSha = "candidate-sha",
                ExpectedStableHeadSha = "stable-sha",
                TrackedPaths = ["src/Service1.cs"],
                UntrackedPaths = ["hone-results/experiment-1", "scratch.txt"],
            });

        FailureContext ctx = DefaultContext(
            targetDir: targetDir,
            queueItemId: null,
            skipQueue: true,
            cleanupManifestPath: manifestPath);

        FailureHandlerResult result = await handler.HandleFailureAsync(ctx);

        _ = result.Success.Should().BeTrue();
        _ = result.CleanupSucceeded.Should().BeTrue();
        _ = result.VerificationSucceeded.Should().BeTrue();
        _ = result.UntrackedPaths.Should().Equal("hone-results/experiment-1", "scratch.txt");

        await vc.Received(1).RemoveUntrackedPathsAsync(
            targetDir,
            Arg.Is<IEnumerable<string>>(paths => !paths.Any()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleFailure_WhenPersistedManifestStableHeadDiffers_ReturnsFailure()
    {
        (ExperimentFailureHandler handler, IVersionControl vc, _, _, IRunStateStore runStateStore, string targetDir) =
            CreateHandler("persisted-manifest-sha-mismatch");

        string manifestPath = runStateStore.GetCleanupManifestPath(1);
        await runStateStore.SaveCleanupManifestAsync(
            manifestPath,
            new CleanupManifest
            {
                Experiment = 1,
                BranchName = "hone/experiment-1",
                BaseBranch = "main",
                CandidateHeadSha = "candidate-sha",
                ExpectedStableHeadSha = "different-stable-sha",
                TrackedPaths = ["src/Service1.cs"],
                UntrackedPaths = [],
            });

        FailureContext ctx = DefaultContext(
            targetDir: targetDir,
            queueItemId: null,
            skipQueue: true,
            cleanupManifestPath: manifestPath);

        FailureHandlerResult result = await handler.HandleFailureAsync(ctx);

        _ = result.Success.Should().BeFalse();
        _ = result.CleanupSucceeded.Should().BeFalse();
        _ = result.VerificationSucceeded.Should().BeFalse();
        _ = result.FailureMessage.Should().Contain("expects stable head");

        await vc.DidNotReceive().RestoreTrackedPathsAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleFailure_VerificationFailure_ReturnsExplicitResult()
    {
        (ExperimentFailureHandler handler, IVersionControl vc, _, _, _, string targetDir) =
            CreateHandler("verification-failure");

        _ = vc.IsWorkingTreeCleanAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        FailureContext ctx = DefaultContext(targetDir: targetDir, queueItemId: null, skipQueue: true);

        FailureHandlerResult result = await handler.HandleFailureAsync(ctx);

        _ = result.Success.Should().BeFalse();
        _ = result.CleanupSucceeded.Should().BeTrue();
        _ = result.VerificationSucceeded.Should().BeFalse();
        _ = result.WorktreeCleanAfterCleanup.Should().BeFalse();
        _ = result.FailureMessage.Should().Contain("still dirty");
    }

    [Fact]
    public async Task HandleFailure_EmitsCompletionEvent()
    {
        (ExperimentFailureHandler handler, _, IHoneEventSink sink, _, _, string targetDir) =
            CreateHandler("emit-event");

        FailureContext ctx = DefaultContext(
            targetDir: targetDir,
            queueItemId: null,
            skipMetadata: true,
            skipQueue: true);

        _ = await handler.HandleFailureAsync(ctx);

        sink.Received().Emit(Arg.Is<StatusMessage>(e =>
            e.Message.Contains("Failure handler completed", StringComparison.Ordinal) &&
            e.Experiment == 1));
    }

    [Fact]
    public async Task HandleFailure_CleanupFails_EmitsWarning()
    {
        (ExperimentFailureHandler handler, IVersionControl vc, IHoneEventSink sink, _, _, string targetDir) =
            CreateHandler("emit-warning");

        _ = vc.GetTouchedTrackedPathsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("cleanup broke"));

        FailureContext ctx = DefaultContext(
            targetDir: targetDir,
            queueItemId: null,
            skipMetadata: true,
            skipQueue: true);

        _ = await handler.HandleFailureAsync(ctx);

        sink.Received().Emit(Arg.Is<StatusMessage>(e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("Cleanup failed", StringComparison.Ordinal)));
    }
}
