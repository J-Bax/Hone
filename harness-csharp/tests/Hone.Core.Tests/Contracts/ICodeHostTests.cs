using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Hone.Core.Contracts;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Contracts;

public sealed class ICodeHostTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void PushAndPr_Present()
    {
        System.Type iface = typeof(ICodeHost);

        _ = iface.GetMethod("PushBranchAsync").Should().NotBeNull();
        _ = iface.GetMethod("CreatePullRequestAsync").Should().NotBeNull();
        _ = iface.GetMethod("GetPullRequestStatusAsync").Should().NotBeNull();
    }

    [Fact]
    public void PushBranchAsync_ReturnsPushResult()
    {
        MethodInfo? method = typeof(ICodeHost).GetMethod("PushBranchAsync");

        _ = method!.ReturnType.Should().Be<Task<PushResult>>();
    }

    [Fact]
    public void CreatePullRequestAsync_ReturnsPullRequestResult()
    {
        MethodInfo? method = typeof(ICodeHost).GetMethod("CreatePullRequestAsync");

        _ = method!.ReturnType.Should().Be<Task<PullRequestResult>>();
    }

    [Fact]
    public void GetPullRequestStatusAsync_ReturnsPullRequestStatus()
    {
        MethodInfo? method = typeof(ICodeHost).GetMethod("GetPullRequestStatusAsync");

        _ = method!.ReturnType.Should().Be<Task<PullRequestStatus>>();
    }

    [Fact]
    public void PushResult_RoundTrips_ThroughJson()
    {
        PushResult original = new(Success: true, Output: "pushed to origin/main");

        string json = JsonSerializer.Serialize(original);
        PushResult? deserialized = JsonSerializer.Deserialize<PushResult>(json);

        _ = deserialized.Should().Be(original);
    }

    [Fact]
    public void CreatePrOptions_RoundTrips_ThroughJson()
    {
        CreatePrOptions original = new(
            BaseBranch: "main",
            HeadBranch: "feature/optimize",
            Title: "Optimize query",
            Body: "Improves p99 latency",
            WorkingDirectory: "/repo");

        string json = JsonSerializer.Serialize(original);
        CreatePrOptions? deserialized = JsonSerializer.Deserialize<CreatePrOptions>(json);

        _ = deserialized.Should().Be(original);
    }

    [Fact]
    public void CreatePrOptions_RoundTrips_WithNullWorkingDirectory()
    {
        CreatePrOptions original = new(
            BaseBranch: "main",
            HeadBranch: "feature/fix",
            Title: "Fix",
            Body: "Fix it");

        string json = JsonSerializer.Serialize(original);
        CreatePrOptions? deserialized = JsonSerializer.Deserialize<CreatePrOptions>(json);

        _ = deserialized.Should().Be(original);
        _ = deserialized!.WorkingDirectory.Should().BeNull();
    }

    [Fact]
    public void PullRequestResult_RoundTrips_ThroughJson()
    {
        PullRequestResult original = new(
            Success: true,
            PrNumber: 42,
            PrUrl: new Uri("https://github.com/org/repo/pull/42"));

        string json = JsonSerializer.Serialize(original);
        PullRequestResult? deserialized = JsonSerializer.Deserialize<PullRequestResult>(json);

        _ = deserialized.Should().Be(original);
    }

    [Fact]
    public void PullRequestResult_RoundTrips_WithNullOptionals()
    {
        PullRequestResult original = new(
            Success: false,
            PrNumber: null,
            PrUrl: null);

        string json = JsonSerializer.Serialize(original);
        PullRequestResult? deserialized = JsonSerializer.Deserialize<PullRequestResult>(json);

        _ = deserialized.Should().Be(original);
    }

    [Fact]
    public void PullRequestStatus_RoundTrips_ThroughJson()
    {
        PullRequestStatus original = new(
            PrNumber: 42,
            State: "open",
            Merged: false);

        string json = JsonSerializer.Serialize(original);
        PullRequestStatus? deserialized = JsonSerializer.Deserialize<PullRequestStatus>(json);

        _ = deserialized.Should().Be(original);
    }
}
