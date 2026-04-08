using FluentAssertions;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.TestInfrastructure;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Measurement.K6.Tests;

public sealed class K6LoadTestRunnerTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private const string FixtureJson = """
        {
          "metrics": {
            "http_req_duration": {
              "min": 1.5, "med": 100.0, "max": 500.0,
              "p(90)": 200.0, "p(95)": 300.0, "p(99)": 450.0, "avg": 150.0
            },
            "http_reqs": { "count": 1000, "rate": 50.5 },
            "http_req_failed": { "passes": 10, "fails": 990, "value": 0.01 }
          }
        }
        """;

    [Fact]
    public async Task RunAsync_SuccessfulRun_ReturnsMetrics()
    {
        // Arrange
        string outputDir = Path.Combine(TempDir, "output");
        string expectedSummary = Path.Combine(outputDir, "k6-summary-run1.json");
        LoadTestOptions options = CreateOptions(outputDir, run: 1);

        IProcessRunner processRunner = Substitute.For<IProcessRunner>();
        _ = processRunner.RunAsync(
            executable: "k6",
            arguments: Arg.Any<IEnumerable<string>>(),
            workingDirectory: null,
            timeout: Arg.Any<TimeSpan?>(),
            ct: Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                Directory.CreateDirectory(outputDir);
                File.WriteAllTextAsync(expectedSummary, FixtureJson).GetAwaiter().GetResult();
                return new ProcessResult(Success: true, Output: "k6 output", ExitCode: 0, TimedOut: false);
            });

        K6LoadTestRunner sut = new(processRunner);

        // Act
        LoadTestResult result = await sut.RunAsync(options);

        // Assert
        _ = result.Success.Should().BeTrue();
        _ = result.Metrics.Should().NotBeNull();
        _ = result.Metrics!.Experiment.Should().Be(1);
        _ = result.Metrics.Run.Should().Be(1);
        _ = result.Metrics.HttpReqDuration.Avg.Should().Be(150.0);
        _ = result.Metrics.HttpReqs.Count.Should().Be(1000);
        _ = result.SummaryPath.Should().Be(expectedSummary);
        _ = result.Output.Should().Be("k6 output");
    }

    [Fact]
    public async Task RunAsync_Timeout_ReturnsFailure()
    {
        // Arrange
        string outputDir = Path.Combine(TempDir, "output");
        LoadTestOptions options = CreateOptions(outputDir, run: 1, timeout: TimeSpan.FromSeconds(30));

        IProcessRunner processRunner = Substitute.For<IProcessRunner>();
        _ = processRunner.RunAsync(
            executable: "k6",
            arguments: Arg.Any<IEnumerable<string>>(),
            workingDirectory: null,
            timeout: Arg.Any<TimeSpan?>(),
            ct: Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(Success: false, Output: "", ExitCode: -1, TimedOut: true));

        K6LoadTestRunner sut = new(processRunner);

        // Act
        LoadTestResult result = await sut.RunAsync(options);

        // Assert
        _ = result.Success.Should().BeFalse();
        _ = result.Metrics.Should().BeNull();
        _ = result.SummaryPath.Should().BeNull();
        _ = result.Output.Should().Contain("timed out");
    }

    [Fact]
    public async Task RunAsync_NoSummaryFile_ReturnsFailure()
    {
        // Arrange
        string outputDir = Path.Combine(TempDir, "output");
        LoadTestOptions options = CreateOptions(outputDir, run: 1);

        IProcessRunner processRunner = Substitute.For<IProcessRunner>();
        _ = processRunner.RunAsync(
            executable: "k6",
            arguments: Arg.Any<IEnumerable<string>>(),
            workingDirectory: null,
            timeout: Arg.Any<TimeSpan?>(),
            ct: Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(Success: true, Output: "no summary", ExitCode: 0, TimedOut: false));

        K6LoadTestRunner sut = new(processRunner);

        // Act
        LoadTestResult result = await sut.RunAsync(options);

        // Assert
        _ = result.Success.Should().BeFalse();
        _ = result.Metrics.Should().BeNull();
        _ = result.SummaryPath.Should().BeNull();
        _ = result.Output.Should().Be("no summary");
    }

    [Fact]
    public async Task RunAsync_DynamicPort_SubstitutesBaseUrl()
    {
        // Arrange
        string outputDir = Path.Combine(TempDir, "output");
        Uri baseUrl = new("http://localhost:9876");
        LoadTestOptions options = new(
            ScenarioPath: "test.js",
            BaseUrl: baseUrl,
            OutputDir: outputDir,
            Experiment: 1,
            Run: 1,
            Timeout: null);

        IProcessRunner processRunner = Substitute.For<IProcessRunner>();
        _ = processRunner.RunAsync(
            executable: "k6",
            arguments: Arg.Any<IEnumerable<string>>(),
            workingDirectory: null,
            timeout: Arg.Any<TimeSpan?>(),
            ct: Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(Success: true, Output: "", ExitCode: 0, TimedOut: false));

        K6LoadTestRunner sut = new(processRunner);

        // Act
        _ = await sut.RunAsync(options);

        // Assert — verify the BASE_URL argument was passed correctly
        _ = await processRunner.Received(1).RunAsync(
            executable: "k6",
            arguments: Arg.Is<IEnumerable<string>>(args =>
                args.Contains($"BASE_URL={baseUrl.GetLeftPart(UriPartial.Authority)}", StringComparer.Ordinal)),
            workingDirectory: null,
            timeout: Arg.Any<TimeSpan?>(),
            ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_EnvironmentVars_PassedToK6()
    {
        // Arrange
        string outputDir = Path.Combine(TempDir, "output");
        Dictionary<string, string> envVars = new(StringComparer.Ordinal)
        {
            ["API_KEY"] = "secret123",
            ["REGION"] = "us-west-2",
        };

        LoadTestOptions options = new(
            ScenarioPath: "scenario.js",
            BaseUrl: new Uri("http://localhost:5000"),
            OutputDir: outputDir,
            Experiment: 1,
            Run: 1,
            Timeout: null,
            EnvironmentVars: envVars);

        IProcessRunner processRunner = Substitute.For<IProcessRunner>();
        _ = processRunner.RunAsync(
            executable: "k6",
            arguments: Arg.Any<IEnumerable<string>>(),
            workingDirectory: null,
            timeout: Arg.Any<TimeSpan?>(),
            ct: Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(Success: true, Output: "", ExitCode: 0, TimedOut: false));

        K6LoadTestRunner sut = new(processRunner);

        // Act
        _ = await sut.RunAsync(options);

        // Assert — verify extra env vars are passed
        _ = await processRunner.Received(1).RunAsync(
            executable: "k6",
            arguments: Arg.Is<IEnumerable<string>>(args =>
                args.Contains("API_KEY=secret123", StringComparer.Ordinal) &&
                args.Contains("REGION=us-west-2", StringComparer.Ordinal)),
            workingDirectory: null,
            timeout: Arg.Any<TimeSpan?>(),
            ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_CreatesOutputDirectory()
    {
        // Arrange
        string outputDir = Path.Combine(TempDir, "new-output-dir");
        LoadTestOptions options = CreateOptions(outputDir, run: 1);

        _ = Directory.Exists(outputDir).Should().BeFalse("output dir should not exist before the test");

        IProcessRunner processRunner = Substitute.For<IProcessRunner>();
        _ = processRunner.RunAsync(
            executable: "k6",
            arguments: Arg.Any<IEnumerable<string>>(),
            workingDirectory: null,
            timeout: Arg.Any<TimeSpan?>(),
            ct: Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(Success: true, Output: "", ExitCode: 0, TimedOut: false));

        K6LoadTestRunner sut = new(processRunner);

        // Act
        _ = await sut.RunAsync(options);

        // Assert
        _ = Directory.Exists(outputDir).Should().BeTrue("RunAsync should create the output directory");
    }

    private static LoadTestOptions CreateOptions(
        string outputDir,
        int run,
        TimeSpan? timeout = null)
    {
        return new LoadTestOptions(
            ScenarioPath: "scenario.js",
            BaseUrl: new Uri("http://localhost:5000"),
            OutputDir: outputDir,
            Experiment: 1,
            Run: run,
            Timeout: timeout);
    }
}
