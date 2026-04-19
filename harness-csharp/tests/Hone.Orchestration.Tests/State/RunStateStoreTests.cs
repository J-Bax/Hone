using System.Text.Json;
using FluentAssertions;
using Hone.Orchestration.State;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Orchestration.Tests.State;

public sealed class RunStateStoreTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private const string MetadataPath = ".hone\\results\\metadata";

    [Fact]
    public async Task LoadAsync_WhenRunStateIsMissing_ReturnsNull()
    {
        string targetDir = CreateTargetDir("run-state-missing");
        var store = new RunStateStore(targetDir, MetadataPath);

        RunStateDocument? document = await store.LoadAsync().ConfigureAwait(true);

        _ = document.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_RoundTripsRunStateDocument()
    {
        string targetDir = CreateTargetDir("run-state-roundtrip");
        var store = new RunStateStore(targetDir, MetadataPath);
        string cleanupManifestPath = store.GetCleanupManifestPath(8);

        var document = new RunStateDocument
        {
            StableBranch = "hone/experiment-7",
            StableHeadSha = "3d1dfbf",
            Status = RecoveryState.CandidateCommitted,
            CurrentExperiment = new CurrentExperimentState
            {
                Number = 8,
                QueueItemId = "2",
                BranchName = "hone/experiment-8",
                BaseBranch = "hone/experiment-7",
                CandidateHeadSha = "8b7a123",
                CleanupManifestPath = cleanupManifestPath,
                Phase = RecoveryState.CandidateCommitted,
                StartedAt = "2026-04-18T10:16:17Z",
            },
        };

        await store.SaveAsync(document).ConfigureAwait(true);
        RunStateDocument? loaded = await store.LoadAsync().ConfigureAwait(true);

        _ = loaded.Should().BeEquivalentTo(document);
        _ = File.Exists(store.RunStatePath).Should().BeTrue();
        _ = Directory.GetFiles(Path.Combine(targetDir, MetadataPath), "*.tmp", SearchOption.TopDirectoryOnly)
            .Should().BeEmpty("atomic writes should not leave temporary files behind");

        string json = await File.ReadAllTextAsync(store.RunStatePath).ConfigureAwait(true);
        using var parsed = JsonDocument.Parse(json);
        JsonElement root = parsed.RootElement;

        _ = root.GetProperty("schemaVersion").GetInt32().Should().Be(RunStateDocument.CurrentSchemaVersion);
        _ = root.GetProperty("status").GetString().Should().Be("candidate_committed");
        _ = root.GetProperty("currentExperiment").GetProperty("phase").GetString().Should().Be("candidate_committed");
    }

    [Fact]
    public async Task SaveCleanupManifestAsync_RoundTripsCleanupManifest()
    {
        string targetDir = CreateTargetDir("cleanup-manifest-roundtrip");
        var store = new RunStateStore(targetDir, MetadataPath);
        string manifestPath = store.GetCleanupManifestPath(8);

        var manifest = new CleanupManifest
        {
            Experiment = 8,
            BranchName = "hone/experiment-8",
            BaseBranch = "hone/experiment-7",
            CandidateHeadSha = "8b7a123",
            TrackedPaths =
            [
                "SampleApi\\Controllers\\WeatherForecastController.cs",
                "SampleApi\\Services\\CacheService.cs",
            ],
            UntrackedPaths =
            [
                ".hone\\results\\experiment-8\\diagnostics\\dotnet-counters.json",
            ],
        };

        await store.SaveCleanupManifestAsync(manifestPath, manifest).ConfigureAwait(true);
        CleanupManifest? loaded = await store.LoadCleanupManifestAsync(manifestPath).ConfigureAwait(true);

        _ = loaded.Should().BeEquivalentTo(manifest);
        _ = File.Exists(Path.Combine(targetDir, manifestPath)).Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_WithUnsupportedSchemaVersion_Throws()
    {
        string targetDir = CreateTargetDir("run-state-schema");
        var store = new RunStateStore(targetDir, MetadataPath);

        Directory.CreateDirectory(Path.GetDirectoryName(store.RunStatePath)!);
        await File.WriteAllTextAsync(
            store.RunStatePath,
            """{"schemaVersion":99,"stableBranch":"hone/experiment-7","stableHeadSha":"3d1dfbf","status":"idle"}""")
            .ConfigureAwait(true);

        Func<Task> act = async () => _ = await store.LoadAsync().ConfigureAwait(true);

        _ = await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Unsupported schema version*")
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task LoadAsync_WithInvalidJson_Throws()
    {
        string targetDir = CreateTargetDir("run-state-invalid-json");
        var store = new RunStateStore(targetDir, MetadataPath);

        Directory.CreateDirectory(Path.GetDirectoryName(store.RunStatePath)!);
        await File.WriteAllTextAsync(store.RunStatePath, "{\"schemaVersion\":1,\"stableBranch\":\"main\"")
            .ConfigureAwait(true);

        Func<Task> act = async () => _ = await store.LoadAsync().ConfigureAwait(true);

        _ = await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Failed to parse run state document*")
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task LoadCleanupManifestAsync_WithUnsupportedSchemaVersion_Throws()
    {
        string targetDir = CreateTargetDir("cleanup-manifest-schema");
        var store = new RunStateStore(targetDir, MetadataPath);
        string manifestPath = store.GetCleanupManifestPath(8);
        string fullManifestPath = Path.Combine(targetDir, manifestPath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullManifestPath)!);
        await File.WriteAllTextAsync(
            fullManifestPath,
            """{"schemaVersion":99,"experiment":8,"branchName":"hone/experiment-8","baseBranch":"main"}""")
            .ConfigureAwait(true);

        Func<Task> act = async () => _ = await store.LoadCleanupManifestAsync(manifestPath).ConfigureAwait(true);

        _ = await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Unsupported schema version*")
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task LoadCleanupManifestAsync_WithInvalidJson_Throws()
    {
        string targetDir = CreateTargetDir("cleanup-manifest-invalid-json");
        var store = new RunStateStore(targetDir, MetadataPath);
        string manifestPath = store.GetCleanupManifestPath(8);
        string fullManifestPath = Path.Combine(targetDir, manifestPath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullManifestPath)!);
        await File.WriteAllTextAsync(fullManifestPath, "{\"schemaVersion\":1,\"experiment\":8")
            .ConfigureAwait(true);

        Func<Task> act = async () => _ = await store.LoadCleanupManifestAsync(manifestPath).ConfigureAwait(true);

        _ = await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Failed to parse cleanup manifest*")
            .ConfigureAwait(true);
    }

    [Fact]
    public void GetCleanupManifestPath_UsesConfiguredMetadataPath()
    {
        string targetDir = CreateTargetDir("cleanup-manifest-path");
        var store = new RunStateStore(targetDir, MetadataPath);

        string manifestPath = store.GetCleanupManifestPath(3);

        _ = manifestPath.Should().Be(Path.Combine(MetadataPath, "cleanup", "experiment-3.json"));
    }
}
