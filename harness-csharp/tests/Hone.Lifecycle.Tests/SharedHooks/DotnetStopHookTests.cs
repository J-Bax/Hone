using FluentAssertions;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Lifecycle.SharedHooks;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Lifecycle.Tests.SharedHooks;

public sealed class DotnetStopHookTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private static HookContext CreateContext(string? projectPath = null) =>
        new(
            TargetPath: "C:\\nonexistent\\target",
            Config: new HoneConfig(Api: new ApiConfig(
                ProjectPath: projectPath ?? "some-api/SomeApi")),
            BaseUrl: null,
            Experiment: 0);

    [Fact]
    public async Task ExecuteAsync_NoProcessesFound_ReturnsSuccess()
    {
        var sut = new DotnetStopHook();
        HookContext context = CreateContext();

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Success.Should().BeTrue();
        _ = result.Message.Should().Be("No running target API processes found");
        _ = result.Duration.Should().BePositive();
        _ = result.Artifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_EmptyProjectPath_ReturnsSuccess()
    {
        var sut = new DotnetStopHook();
        HookContext context = CreateContext(projectPath: "");

        HookResult result = await sut.ExecuteAsync(context);

        _ = result.Success.Should().BeTrue();
        _ = result.Message.Should().Be("No running target API processes found");
    }

    [Fact]
    public async Task ExecuteAsync_NullContext_ThrowsArgumentNullException()
    {
        var sut = new DotnetStopHook();

        Func<Task> act = () => sut.ExecuteAsync(null!);

        _ = await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void FindTargetProcesses_NullPath_ReturnsEmpty()
    {
        List<System.Diagnostics.Process> result = DotnetStopHook.FindTargetProcesses(projectPath: null);

        _ = result.Should().BeEmpty();
    }

    [Fact]
    public void FindTargetProcesses_EmptyPath_ReturnsEmpty()
    {
        List<System.Diagnostics.Process> result = DotnetStopHook.FindTargetProcesses("");

        _ = result.Should().BeEmpty();
    }

    [Fact]
    public void FindTargetProcesses_NonexistentPath_ReturnsEmpty()
    {
        List<System.Diagnostics.Process> result = DotnetStopHook.FindTargetProcesses(
            projectPath: @"C:\nonexistent\path\that\does\not\exist");

        _ = result.Should().BeEmpty();
    }
}
