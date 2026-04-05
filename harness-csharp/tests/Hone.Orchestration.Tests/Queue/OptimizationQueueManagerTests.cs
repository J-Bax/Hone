using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Hone.Core.Observability;
using Hone.Orchestration.Queue;
using Hone.TestInfrastructure;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Orchestration.Tests.Queue;

public sealed class OptimizationQueueManagerTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private static List<Opportunity> CreateTestOpportunities(int count = 3)
    {
        return
        [
            .. Enumerable.Range(1, count)
                .Select(i => new Opportunity(
                    $"src/Service{i}.cs",
                    $"Optimize Service{i}",
                    $"Explanation for Service{i}",
                    i == 2 ? OpportunityScope.Architecture : OpportunityScope.Narrow,
                    RootCause: i == 1 ? "Root cause details for item 1" : null,
                    ImpactEstimate: null)),
        ];
    }

    private (OptimizationQueueManager Manager, IHoneEventSink Sink, string MetadataDir) CreateManager(string name)
    {
        string metadataDir = CreateTargetDir(name);
        IHoneEventSink sink = Substitute.For<IHoneEventSink>();
        var manager = new OptimizationQueueManager(metadataDir, sink);
        return (manager, sink, metadataDir);
    }

    [Fact]
    public void Init_CreatesQueueFile()
    {
        // Arrange
        (OptimizationQueueManager mgr, IHoneEventSink sink, string dir) = CreateManager("init-creates");
        List<Opportunity> opps = CreateTestOpportunities();

        // Act
        InitializeResult result = mgr.Initialize(opps, 0);

        // Assert
        _ = result.Success.Should().BeTrue();
        _ = result.Count.Should().Be(3);

        string jsonPath = Path.Combine(dir, "experiment-queue.json");
        _ = File.Exists(jsonPath).Should().BeTrue("queue JSON file should be created");

        string mdPath = Path.Combine(dir, "experiment-queue.md");
        _ = File.Exists(mdPath).Should().BeTrue("queue markdown file should be created");

        // Verify JSON structure
        string json = File.ReadAllText(jsonPath, Encoding.UTF8);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        _ = root.GetProperty("generatedByExperiment").GetInt32().Should().Be(0);
        _ = root.GetProperty("generatedAt").GetString().Should().NotBeNullOrEmpty();
        _ = root.GetProperty("items").GetArrayLength().Should().Be(3);

        // Verify first item shape
        JsonElement firstItem = root.GetProperty("items")[0];
        _ = firstItem.GetProperty("id").GetString().Should().Be("1");
        _ = firstItem.GetProperty("filePath").GetString().Should().Be("src/Service1.cs");
        _ = firstItem.GetProperty("status").GetString().Should().Be("pending");
        _ = firstItem.GetProperty("scope").GetString().Should().Be("narrow");

        // Verify root-cause file was created for item 1
        string rcaPath = Path.Combine(dir, "root-causes", "rca-1.md");
        _ = File.Exists(rcaPath).Should().BeTrue("root cause doc should be saved");
        string rcaContent = File.ReadAllText(rcaPath, Encoding.UTF8);
        _ = rcaContent.Should().Contain("Root cause details for item 1");

        // Verify event was emitted
        sink.Received(1).Emit(Arg.Is<StatusMessage>(e =>
            e.Message.Contains("3 items", StringComparison.Ordinal)));
    }

    [Fact]
    public void Init_NullOpportunities_ReturnsFailure()
    {
        (OptimizationQueueManager mgr, _, _) = CreateManager("init-null");

        InitializeResult result = mgr.Initialize(null!, 0);

        _ = result.Success.Should().BeFalse();
        _ = result.Count.Should().Be(0);
    }

    [Fact]
    public void Init_EmptyOpportunities_ReturnsFailure()
    {
        (OptimizationQueueManager mgr, _, _) = CreateManager("init-empty");

        InitializeResult result = mgr.Initialize([], 0);

        _ = result.Success.Should().BeFalse();
        _ = result.Count.Should().Be(0);
    }

    [Fact]
    public void GetNext_ReturnsPendingNarrow()
    {
        // Arrange — items: #1 narrow, #2 architecture, #3 narrow
        (OptimizationQueueManager mgr, _, _) = CreateManager("getnext-narrow");
        _ = mgr.Initialize(CreateTestOpportunities(), 0);

        // Act
        QueueItem? next = mgr.GetNext(1);

        // Assert — should skip architecture (#2) and return #1
        _ = next.Should().NotBeNull();
        _ = next!.Id.Should().Be("1");
        _ = next.Scope.Should().Be(OpportunityScope.Narrow);
        _ = next.FilePath.Should().Be("src/Service1.cs");
    }

    [Fact]
    public void GetNext_MarksInProgress()
    {
        // Arrange
        (OptimizationQueueManager mgr, _, string dir) = CreateManager("getnext-inprogress");
        _ = mgr.Initialize(CreateTestOpportunities(), 0);

        // Act
        QueueItem? next = mgr.GetNext(1);

        // Assert — returned item should be InProgress
        _ = next.Should().NotBeNull();
        _ = next!.Status.Should().Be(QueueItemStatus.InProgress);

        // Verify the file was updated
        string json = File.ReadAllText(Path.Combine(dir, "experiment-queue.json"), Encoding.UTF8);
        using var doc = JsonDocument.Parse(json);
        JsonElement firstItem = doc.RootElement.GetProperty("items")[0];
        _ = firstItem.GetProperty("status").GetString().Should().Be("in_progress");

        // Verify markdown reflects in-progress state
        string md = File.ReadAllText(Path.Combine(dir, "experiment-queue.md"), Encoding.UTF8);
        _ = md.Should().Contain("*(in progress)*");
    }

    [Fact]
    public void GetNext_AllDoneOrArchitecture_ReturnsNull()
    {
        // Arrange — mark the two narrow items as done
        (OptimizationQueueManager mgr, _, _) = CreateManager("getnext-null");
        _ = mgr.Initialize(CreateTestOpportunities(), 0);
        _ = mgr.GetNext(1); // picks #1, marks in_progress
        mgr.MarkDone("1", "improved", 1);
        _ = mgr.GetNext(2); // picks #3, marks in_progress
        mgr.MarkDone("3", "improved", 2);

        // Act
        QueueItem? next = mgr.GetNext(3);

        // Assert — only architecture (#2) remains pending, should be skipped
        _ = next.Should().BeNull();
    }

    [Fact]
    public void MarkDone_RecordsOutcomeAndExperiment()
    {
        // Arrange
        (OptimizationQueueManager mgr, IHoneEventSink sink, string dir) = CreateManager("markdone");
        _ = mgr.Initialize(CreateTestOpportunities(), 0);
        _ = mgr.GetNext(1); // marks #1 in_progress

        // Act
        mgr.MarkDone("1", "improved", 1);

        // Assert — verify JSON
        string json = File.ReadAllText(Path.Combine(dir, "experiment-queue.json"), Encoding.UTF8);
        using var doc = JsonDocument.Parse(json);
        JsonElement item1 = doc.RootElement.GetProperty("items")[0];
        _ = item1.GetProperty("status").GetString().Should().Be("done");
        _ = item1.GetProperty("triedByExperiment").GetInt32().Should().Be(1);
        _ = item1.GetProperty("outcome").GetString().Should().Be("improved");

        // Verify markdown
        string md = File.ReadAllText(Path.Combine(dir, "experiment-queue.md"), Encoding.UTF8);
        _ = md.Should().Contain("*(experiment 1 \u2014 improved)*");
        _ = md.Should().Contain("[x]");

        // Verify event
        sink.Received().Emit(Arg.Is<StatusMessage>(e =>
            e.Message.Contains("marked done", StringComparison.Ordinal)));
    }

    [Fact]
    public void HasActionable_AllDone_ReturnsFalse()
    {
        // Arrange
        (OptimizationQueueManager mgr, _, _) = CreateManager("has-actionable-false");
        _ = mgr.Initialize(CreateTestOpportunities(), 0);

        // Mark all narrow items as done
        _ = mgr.GetNext(1);
        mgr.MarkDone("1", "improved", 1);
        _ = mgr.GetNext(2);
        mgr.MarkDone("3", "improved", 2);

        // Act
        bool result = mgr.HasActionable();

        // Assert — only architecture item remains, should be false
        _ = result.Should().BeFalse();
    }

    [Fact]
    public void HasActionable_PendingNarrow_ReturnsTrue()
    {
        // Arrange
        (OptimizationQueueManager mgr, _, _) = CreateManager("has-actionable-true");
        _ = mgr.Initialize(CreateTestOpportunities(), 0);

        // Act
        bool result = mgr.HasActionable();

        // Assert
        _ = result.Should().BeTrue();
    }

    [Fact]
    public void AtomicWrite_NoTmpLeftovers()
    {
        // Arrange
        (OptimizationQueueManager mgr, _, string dir) = CreateManager("atomic-no-tmp");
        _ = mgr.Initialize(CreateTestOpportunities(), 0);

        // Act — perform several mutations
        _ = mgr.GetNext(1);
        mgr.MarkDone("1", "improved", 1);
        mgr.SyncMarkdown();

        // Assert — no .tmp files should remain
        string[] tmpFiles = Directory.GetFiles(dir, "*.tmp", SearchOption.TopDirectoryOnly);
        _ = tmpFiles.Should().BeEmpty("atomic writes should not leave .tmp files behind");
    }

    [Fact]
    public void ConcurrentRead_SafeDuringWrite()
    {
        // Arrange
        (OptimizationQueueManager mgr, _, _) = CreateManager("concurrent");
        var opps = new List<Opportunity>(
        [
            .. Enumerable.Range(1, 20)
                .Select(i => new Opportunity(
                    $"src/File{i}.cs",
                    $"Title {i}",
                    $"Explanation {i}",
                    OpportunityScope.Narrow,
                    RootCause: null,
                    ImpactEstimate: null)),
        ]);
        _ = mgr.Initialize(opps, 0);

        // Act — concurrent reads and writes should not throw
        Action act = () => Parallel.For(0, 50, i =>
        {
            switch (i % 4)
            {
                case 0:
                    _ = mgr.HasActionable();
                    break;
                case 1:
                    _ = mgr.GetNext(i);
                    break;
                case 2:
                    mgr.SyncMarkdown();
                    break;
                default:
                    int itemNum = (i % 20) + 1;
                    mgr.MarkDone(
                        itemNum.ToString(CultureInfo.InvariantCulture),
                        "improved",
                        i);
                    break;
            }
        });

        // Assert
        _ = act.Should().NotThrow("concurrent queue access should be thread-safe");
    }

    [Fact]
    public void SyncMarkdown_RegeneratesFromJson()
    {
        // Arrange
        (OptimizationQueueManager mgr, _, string dir) = CreateManager("sync-md");
        _ = mgr.Initialize(CreateTestOpportunities(), 0);

        // Delete the markdown file
        string mdPath = Path.Combine(dir, "experiment-queue.md");
        File.Delete(mdPath);
        _ = File.Exists(mdPath).Should().BeFalse();

        // Act
        mgr.SyncMarkdown();

        // Assert
        _ = File.Exists(mdPath).Should().BeTrue();
        string md = File.ReadAllText(mdPath, Encoding.UTF8);
        _ = md.Should().Contain("# Optimization Queue");
        _ = md.Should().Contain("**#1**");
        _ = md.Should().Contain("src/Service1.cs");
    }
}
