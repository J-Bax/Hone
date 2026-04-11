using FluentAssertions;

using Hone.TestInfrastructure;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Agents.Preparation.Tests;

public sealed class PreProberLegacyHarnessTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public async Task PreProber_DetectsLegacyHarness_WhenConfigPsd1Exists()
    {
        // Arrange
        string targetDir = CreateTargetDir("legacy-psd1", b => b
            .AddFile("config.psd1", "@{ Name = 'test' }")
            .AddFile("MyApp.sln", "solution"));

        // Act
        PreProbeData result = await PreProber.ProbeAsync(targetDir, processRunner: null, CancellationToken.None);

        // Assert
        _ = result.LegacyHarness.Should().NotBeNull();
        _ = result.LegacyHarness!.Detected.Should().BeTrue();
        _ = result.LegacyHarness.ConfigPsd1Path.Should().NotBeNull();
        _ = result.LegacyHarness.ConfigPsd1Path.Should().Contain("config.psd1");
    }

    [Fact]
    public async Task PreProber_DetectsLegacyHarness_WhenInvokeScriptsExist()
    {
        // Arrange
        string targetDir = CreateTargetDir("legacy-invoke", b => b
            .AddFile("Invoke-Build.ps1", "dotnet build")
            .AddFile("MyApp.sln", "solution"));

        // Act
        PreProbeData result = await PreProber.ProbeAsync(targetDir, processRunner: null, CancellationToken.None);

        // Assert
        _ = result.LegacyHarness.Should().NotBeNull();
        _ = result.LegacyHarness!.Detected.Should().BeTrue();
        _ = result.LegacyHarness.HookScripts.Should().NotBeNullOrEmpty();
        _ = result.LegacyHarness.HookScripts.Should().Contain(s => s.Contains("Invoke-Build.ps1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreProber_NoLegacyHarness_WhenNoPsFiles()
    {
        // Arrange
        string targetDir = CreateTargetDir("no-legacy", b => b
            .AddFile("MyApp.sln", "solution")
            .AddFile("README.md", "# readme"));

        // Act
        PreProbeData result = await PreProber.ProbeAsync(targetDir, processRunner: null, CancellationToken.None);

        // Assert
        _ = result.LegacyHarness.Should().NotBeNull();
        _ = result.LegacyHarness!.Detected.Should().BeFalse();
        _ = result.LegacyHarness.ConfigPsd1Path.Should().BeNull();
        _ = result.LegacyHarness.HookScripts.Should().BeNull();
    }
}
