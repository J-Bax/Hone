using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Hone.Core.Contracts;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Contracts;

public sealed class ILoadTestRunnerTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void RunAsync_ReturnsLoadTestResult()
    {
        MethodInfo? method = typeof(ILoadTestRunner).GetMethod("RunAsync");

        _ = method.Should().NotBeNull("ILoadTestRunner must define RunAsync");
        _ = method!.ReturnType.Should().Be<Task<LoadTestResult>>();
    }

    [Fact]
    public void RunAsync_HasExpectedParameters()
    {
        MethodInfo? method = typeof(ILoadTestRunner).GetMethod("RunAsync");
        ParameterInfo[] parameters = method!.GetParameters();

        _ = parameters.Should().HaveCount(2);
        _ = parameters[0].ParameterType.Should().Be<LoadTestOptions>();
        _ = parameters[0].Name.Should().Be("options");
        _ = parameters[1].ParameterType.Should().Be<CancellationToken>();
        _ = parameters[1].Name.Should().Be("ct");
    }

    [Fact]
    public void LoadTestOptions_RoundTrips_ThroughJson()
    {
        LoadTestOptions original = new(
            ScenarioPath: "/scenarios/load.js",
            BaseUrl: new Uri("http://localhost:5000"),
            OutputDir: "/output",
            Experiment: 1,
            Run: 3,
            Timeout: TimeSpan.FromMinutes(10),
            EnvironmentVars: new Dictionary<string, string>(StringComparer.Ordinal) { ["K6_OUT"] = "json" });

        string json = JsonSerializer.Serialize(original);
        LoadTestOptions? deserialized = JsonSerializer.Deserialize<LoadTestOptions>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.ScenarioPath.Should().Be(original.ScenarioPath);
        _ = deserialized.BaseUrl.Should().Be(original.BaseUrl);
        _ = deserialized.OutputDir.Should().Be(original.OutputDir);
        _ = deserialized.Experiment.Should().Be(original.Experiment);
        _ = deserialized.Run.Should().Be(original.Run);
        _ = deserialized.Timeout.Should().Be(original.Timeout);
        _ = deserialized.EnvironmentVars.Should().Contain(
            new KeyValuePair<string, string>("K6_OUT", "json"));
    }

    [Fact]
    public void LoadTestOptions_RoundTrips_WithNullOptionals()
    {
        LoadTestOptions original = new(
            ScenarioPath: "/scenarios/load.js",
            BaseUrl: new Uri("http://localhost:5000"),
            OutputDir: "/output",
            Experiment: 1,
            Run: 1,
            Timeout: null);

        string json = JsonSerializer.Serialize(original);
        LoadTestOptions? deserialized = JsonSerializer.Deserialize<LoadTestOptions>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Timeout.Should().BeNull();
        _ = deserialized.EnvironmentVars.Should().BeNull();
    }

    [Fact]
    public void LoadTestResult_RoundTrips_ThroughJson()
    {
        LoadTestResult original = new(
            Success: true,
            Metrics: null,
            SummaryPath: "/output/summary.json",
            Output: "Load test completed");

        string json = JsonSerializer.Serialize(original);
        LoadTestResult? deserialized = JsonSerializer.Deserialize<LoadTestResult>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Success.Should().BeTrue();
        _ = deserialized.SummaryPath.Should().Be(original.SummaryPath);
        _ = deserialized.Output.Should().Be(original.Output);
    }

    [Fact]
    public void LoadTestResult_RoundTrips_WithNullOptionals()
    {
        LoadTestResult original = new(
            Success: false,
            Metrics: null,
            SummaryPath: null,
            Output: null);

        string json = JsonSerializer.Serialize(original);
        LoadTestResult? deserialized = JsonSerializer.Deserialize<LoadTestResult>(json);

        _ = deserialized.Should().NotBeNull();
        _ = deserialized!.Success.Should().BeFalse();
        _ = deserialized.Metrics.Should().BeNull();
        _ = deserialized.SummaryPath.Should().BeNull();
        _ = deserialized.Output.Should().BeNull();
    }
}
