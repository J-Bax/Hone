using System.Diagnostics;
using System.Text;

using FluentAssertions;

using Hone.Cli;
using Hone.Core.Models;
using Hone.Orchestration.Queue;
using Hone.Orchestration.State;
using Hone.SourceControl.Git;
using Hone.TestInfrastructure;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Integration.Tests;

public sealed class StateCommandTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);

    [Fact]
    public async Task DoctorAndRepairState_WhenIdleQueueLeaseDisagrees_ReleasesLease()
    {
        string targetDir = CreateConfiguredGitTarget("idle-queue-mismatch");
        var git = new GitVersionControl(new ProcessRunner());
        string stableHeadSha = await git.GetHeadShaAsync(targetDir).ConfigureAwait(true);
        Opportunity opportunity = new(FilePath: "src\\Service.cs", Title: "Optimize", Explanation: "Explanation", Scope: OpportunityScope.Narrow, RootCause: null, ImpactEstimate: null);

        OptimizationQueueManager queueManager = CreateQueueManager(targetDir);
        _ = queueManager.Initialize([opportunity], experiment: 1);
        _ = queueManager.GetNext(experiment: 1);

        RunStateStore runStateStore = CreateRunStateStore(targetDir);
        await runStateStore.SaveAsync(new RunStateDocument
        {
            StableBranch = "main",
            StableHeadSha = stableHeadSha,
            Status = RecoveryState.Idle,
        }).ConfigureAwait(true);

        (int doctorExitCode, string doctorOutput, string _) = await RunProgramAsync(
            "doctor", "state", "--target", targetDir).ConfigureAwait(true);

        _ = doctorExitCode.Should().Be(1);
        _ = doctorOutput.Should().Contain("run-state.json is idle");
        _ = doctorOutput.Should().Contain("hone repair state --target");

        (int repairExitCode, string repairOutput, string _) = await RunProgramAsync(
            "repair", "state", "--target", targetDir).ConfigureAwait(true);

        _ = repairExitCode.Should().Be(0);
        _ = repairOutput.Should().Contain("Release queue item '1' back to pending.");

        OptimizationQueue queue = queueManager.GetSnapshot();
        _ = queue.Items.Should().ContainSingle();
        _ = queue.Items[0].Status.Should().Be(QueueItemStatus.Pending);

        RunStateDocument? repairedState = await runStateStore.LoadAsync().ConfigureAwait(true);
        _ = repairedState.Should().NotBeNull();
        _ = repairedState!.Status.Should().Be(RecoveryState.Idle);
        _ = repairedState.CurrentExperiment.Should().BeNull();
    }

    [Fact]
    public async Task RepairState_WhenExperimentBranchRemainsCheckedOut_ReturnsToStableBranchAndClearsLease()
    {
        string targetDir = CreateConfiguredGitTarget("branch-created-repair");
        var git = new GitVersionControl(new ProcessRunner());
        string stableHeadSha = await git.GetHeadShaAsync(targetDir).ConfigureAwait(true);
        Opportunity opportunity = new(FilePath: "src\\Service.cs", Title: "Optimize", Explanation: "Explanation", Scope: OpportunityScope.Narrow, RootCause: null, ImpactEstimate: null);

        _ = RunGit(targetDir, "branch hone/experiment-1");
        _ = RunGit(targetDir, "checkout hone/experiment-1");

        OptimizationQueueManager queueManager = CreateQueueManager(targetDir);
        _ = queueManager.Initialize([opportunity], experiment: 1);
        _ = queueManager.GetNext(experiment: 1);

        RunStateStore runStateStore = CreateRunStateStore(targetDir);
        await runStateStore.SaveAsync(new RunStateDocument
        {
            StableBranch = "main",
            StableHeadSha = stableHeadSha,
            Status = RecoveryState.BranchCreated,
            CurrentExperiment = new CurrentExperimentState
            {
                Number = 1,
                QueueItemId = "1",
                BranchName = "hone/experiment-1",
                BaseBranch = "main",
                Phase = RecoveryState.BranchCreated,
                StartedAt = DateTimeOffset.UtcNow.ToString("o"),
            },
        }).ConfigureAwait(true);

        (int exitCode, string outputText, string _) = await RunProgramAsync(
            "repair", "state", "--target", targetDir).ConfigureAwait(true);

        _ = exitCode.Should().Be(0);
        _ = outputText.Should().Contain("Check out stable branch 'main'");
        _ = outputText.Should().Contain("Save run-state.json as idle after releasing experiment 1.");

        string currentBranch = await git.GetCurrentBranchAsync(targetDir).ConfigureAwait(true);
        _ = currentBranch.Should().Be("main");

        OptimizationQueue queue = queueManager.GetSnapshot();
        _ = queue.Items.Should().ContainSingle();
        _ = queue.Items[0].Status.Should().Be(QueueItemStatus.Pending);

        RunStateDocument? repairedState = await runStateStore.LoadAsync().ConfigureAwait(true);
        _ = repairedState.Should().NotBeNull();
        _ = repairedState!.Status.Should().Be(RecoveryState.Idle);
        _ = repairedState.CurrentExperiment.Should().BeNull();
        _ = repairedState.StableBranch.Should().Be("main");
        _ = repairedState.StableHeadSha.Should().Be(stableHeadSha);
    }

    private static OptimizationQueueManager CreateQueueManager(string targetDir) =>
        new(Path.Combine(targetDir, "hone-results", "metadata"), new Hone.Core.Observability.HoneEventBus());

    private static RunStateStore CreateRunStateStore(string targetDir) =>
        new(targetDir, Path.Combine("hone-results", "metadata"));

    private string CreateConfiguredGitTarget(string name)
    {
        string targetDir = CreateTargetDir(name, builder => _ = builder
            .AddFile(".hone\\config.yaml", """
Name: "StateTarget"
BaseBranch: "main"
Api:
  ResultsPath: "hone-results"
  MetadataPath: "hone-results\\metadata"
""")
            .AddFile(".gitignore", "hone-results/\n")
            .AddFile("src\\Service.cs", "// service"));

        GitTestRepo repo = InitGitRepo(targetDir);
        repo.Configure();
        _ = RunGit(targetDir, "config commit.gpgsign false");
        repo.CommitAll("initial");
        string currentBranch = RunGit(targetDir, "branch --show-current");
        if (!string.Equals(currentBranch, "main", StringComparison.Ordinal))
        {
            _ = RunGit(targetDir, "branch main");
            _ = RunGit(targetDir, "checkout main");
        }

        return targetDir;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProgramAsync(params string[] args)
    {
        await ConsoleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await RunProgramCoreAsync(args).ConfigureAwait(false);
        }
        finally
        {
            _ = ConsoleLock.Release();
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProgramCoreAsync(string[] args)
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;

        Console.SetOut(stdout);
        Console.SetError(stderr);

        try
        {
            int exitCode = await Program.Main(args).ConfigureAwait(false);
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            await stdout.DisposeAsync().ConfigureAwait(false);
            await stderr.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string RunGit(string workingDirectory, string arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _ = process.Start();
        string output = process.StandardOutput.ReadToEnd().Trim();
        string error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {arguments} failed with exit code {process.ExitCode}: {error}");
        }

        return output;
    }
}
