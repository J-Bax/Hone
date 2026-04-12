using FluentAssertions;

using Hone.TestInfrastructure;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Agents.Preparation.Tests;

public sealed class ScaffoldWriterTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private static ScaffoldPlan CreatePlan(IReadOnlyDictionary<string, string> files) =>
        new() { Files = files, Notes = "test plan" };

    // ── Creates files from plan ─────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_CreatesFilesFromPlan()
    {
        string targetDir = CreateTargetDir("write-basic");

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".hone/config.yaml"] = "Name: test",
            [".hone/scenarios/baseline.js"] = "import http from 'k6/http';",
        };

        ScaffoldWriteResult result = await ScaffoldWriter.WriteAsync(
            targetDir, CreatePlan(files), force: false);

        _ = result.Written.Should().HaveCount(2);
        _ = result.Skipped.Should().BeEmpty();

        string configPath = Path.Combine(targetDir, ".hone", "config.yaml");
        _ = File.Exists(configPath).Should().BeTrue();
        _ = (await File.ReadAllTextAsync(configPath)).Should().Be("Name: test");
    }

    // ── Creates intermediate directories ────────────────────────────────

    [Fact]
    public async Task WriteAsync_CreatesIntermediateDirectories()
    {
        string targetDir = CreateTargetDir("write-dirs");

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".hone/hooks/lifecycle/prepare.sh"] = "#!/bin/sh\necho prepare",
        };

        ScaffoldWriteResult result = await ScaffoldWriter.WriteAsync(
            targetDir, CreatePlan(files), force: false);

        _ = result.Written.Should().HaveCount(1);

        string hookPath = Path.Combine(targetDir, ".hone", "hooks", "lifecycle", "prepare.sh");
        _ = File.Exists(hookPath).Should().BeTrue();
    }

    // ── Skips existing files when force=false ───────────────────────────

    [Fact]
    public async Task WriteAsync_SkipsExistingFiles_WhenForceIsFalse()
    {
        string targetDir = CreateTargetDir("write-skip", b => b
            .AddFile(".hone/config.yaml", "existing content"));

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".hone/config.yaml"] = "new content",
            [".hone/scenarios/baseline.js"] = "import http from 'k6/http';",
        };

        ScaffoldWriteResult result = await ScaffoldWriter.WriteAsync(
            targetDir, CreatePlan(files), force: false);

        _ = result.Written.Should().HaveCount(1);
        _ = result.Written.Should().Contain(".hone/scenarios/baseline.js");
        _ = result.Skipped.Should().HaveCount(1);
        _ = result.Skipped.Should().Contain(".hone/config.yaml");

        // Existing file should NOT be overwritten
        string configPath = Path.Combine(targetDir, ".hone", "config.yaml");
        _ = (await File.ReadAllTextAsync(configPath)).Should().Be("existing content");
    }

    // ── Overwrites existing files when force=true ───────────────────────

    [Fact]
    public async Task WriteAsync_OverwritesExistingFiles_WhenForceIsTrue()
    {
        string targetDir = CreateTargetDir("write-force", b => b
            .AddFile(".hone/config.yaml", "existing content"));

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".hone/config.yaml"] = "new content",
        };

        ScaffoldWriteResult result = await ScaffoldWriter.WriteAsync(
            targetDir, CreatePlan(files), force: true);

        _ = result.Written.Should().HaveCount(1);
        _ = result.Written.Should().Contain(".hone/config.yaml");
        _ = result.Skipped.Should().BeEmpty();

        string configPath = Path.Combine(targetDir, ".hone", "config.yaml");
        _ = (await File.ReadAllTextAsync(configPath)).Should().Be("new content");
    }

    // ── Returns correct written/skipped lists ───────────────────────────

    [Fact]
    public async Task WriteAsync_ReturnsCorrectWrittenAndSkippedLists()
    {
        string targetDir = CreateTargetDir("write-lists", b => b
            .AddFile(".hone/config.yaml", "existing"));

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".hone/config.yaml"] = "new config",
            [".hone/hooks/build.sh"] = "#!/bin/sh\ndotnet build",
            [".hone/scenarios/baseline.js"] = "// k6 scenario",
        };

        ScaffoldWriteResult result = await ScaffoldWriter.WriteAsync(
            targetDir, CreatePlan(files), force: false);

        _ = result.Written.Should().HaveCount(2);
        _ = result.Written.Should().Contain(".hone/hooks/build.sh");
        _ = result.Written.Should().Contain(".hone/scenarios/baseline.js");
        _ = result.Skipped.Should().HaveCount(1);
        _ = result.Skipped.Should().Contain(".hone/config.yaml");
    }

    [Fact]
    public async Task WriteAsync_RejectsTraversalPaths()
    {
        string targetDir = CreateTargetDir("write-traversal");

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["../outside.txt"] = "escaped",
        };

        Func<Task> act = () => ScaffoldWriter.WriteAsync(
            targetDir, CreatePlan(files), force: false);

        FluentAssertions.Specialized.ExceptionAssertions<ArgumentException> assertion =
            await act.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
        _ = assertion.Which.Message.Should().Contain("escapes the target root");
    }

    [Fact]
    public async Task WriteAsync_RejectsRootedPaths()
    {
        string targetDir = CreateTargetDir("write-rooted");
        string rootedPath = Path.Combine(Path.GetPathRoot(targetDir)!, "outside.txt");

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [rootedPath] = "escaped",
        };

        Func<Task> act = () => ScaffoldWriter.WriteAsync(
            targetDir, CreatePlan(files), force: false);

        FluentAssertions.Specialized.ExceptionAssertions<ArgumentException> assertion =
            await act.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
        _ = assertion.Which.Message.Should().Contain("cannot be rooted");
    }
}
