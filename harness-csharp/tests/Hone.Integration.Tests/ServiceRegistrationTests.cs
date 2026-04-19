using FluentAssertions;
using Hone.Cli;
using Hone.Core.Config;
using Hone.Orchestration.State;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Integration.Tests;

public sealed class ServiceRegistrationTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void Build_RegistersRunStateStoreAtMetadataPath()
    {
        string targetDir = CreateTargetDir("service-registration-run-state", builder =>
            _ = builder.AddFile(".hone\\config.yaml", string.Empty));
        string configPath = Path.Combine(targetDir, ".hone", "config.yaml");
        HoneConfig config = new(
            Api: new ApiConfig(
                ResultsPath: ".hone\\results",
                MetadataPath: ".hone\\results\\metadata"));

        IServiceProvider services = ServiceRegistration.Build(targetDir, config, configPath);

        var runStateStore = services.GetService(typeof(IRunStateStore)) as IRunStateStore;
        var concreteStore = services.GetService(typeof(RunStateStore)) as RunStateStore;

        _ = runStateStore.Should().NotBeNull();
        _ = concreteStore.Should().BeSameAs(runStateStore);
        _ = runStateStore!.RunStatePath.Should().Be(
            Path.Combine(targetDir, ".hone\\results\\metadata", "run-state.json"));
        _ = runStateStore.GetCleanupManifestPath(5).Should().Be(
            Path.Combine(".hone\\results\\metadata", "cleanup", "experiment-5.json"));
    }
}
