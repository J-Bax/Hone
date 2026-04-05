using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Xunit;

namespace Hone.Core.Tests.Models;

public sealed class SerializationRoundTripTests
{
    public static TheoryData<object> RoundTripTestCases =>
    [
        // HttpReqCountMetrics
        new HttpReqCountMetrics(Count: 15000, Rate: 125.5),
        new HttpReqCountMetrics(Count: 0, Rate: 0.0),

        // HttpReqDurationMetrics
        new HttpReqDurationMetrics(Avg: 12.5, P50: 10.2, P90: 25.1, P95: 35.7, P99: 85.3, Max: 120.5),
        new HttpReqDurationMetrics(Avg: 0.0, P50: 0.0, P90: 0.0, P95: 0.0, P99: 0.0, Max: 0.0),

        // HttpReqFailedMetrics
        new HttpReqFailedMetrics(Count: 3, Rate: 0.0002),
        new HttpReqFailedMetrics(Count: 0, Rate: 0.0),

        // ProcessResult
        new ProcessResult(Success: true, Output: "Build succeeded.", ExitCode: 0, TimedOut: false),
        new ProcessResult(Success: false, Output: "Timeout after 60s", ExitCode: -1, TimedOut: true),

        // MetricComparison
        new MetricComparison(MetricName: "p95", Current: 35.7, Previous: 42.1, Baseline: 40.0,
            DeltaPct: -15.2, AbsoluteDelta: -6.4, Improved: true, Regressed: false),
        new MetricComparison(MetricName: "rps", Current: 125.5, Previous: 120.0, Baseline: null,
            DeltaPct: 4.6, AbsoluteDelta: 5.5, Improved: true, Regressed: false),

        // IterationAttempt
        new IterationAttempt(Attempt: 1, Stage: "implement", Outcome: "success", DiffLines: 42),
        new IterationAttempt(Attempt: 3, Stage: "verify", Outcome: "no-change", DiffLines: 0),

        // CollectorHandle (Handle is [JsonIgnore]; null round-trips correctly)
        new CollectorHandle(Success: true, Handle: null),

        // MachineInfo
        new MachineInfo(CpuName: "Intel Core i9-13900K", CpuCores: 24, TotalRamGB: 64.0m,
            OsVersion: "Windows 11 23H2", DotnetVersion: "10.0.0"),
        new MachineInfo(CpuName: null, CpuCores: null, TotalRamGB: null, OsVersion: null, DotnetVersion: null),
        new MachineInfo(CpuName: "Apple M2 Pro", CpuCores: 12, TotalRamGB: null,
            OsVersion: null, DotnetVersion: "10.0.0"),

        // Opportunity
        new Opportunity(FilePath: "src/Api/Controllers/OrderController.cs",
            Title: "Reduce N+1 queries in order listing",
            Explanation: "Each order triggers a separate DB query for customer data",
            Scope: OpportunityScope.Narrow, RootCause: "Missing eager loading",
            ImpactEstimate: "~30% latency reduction on /api/orders"),
        new Opportunity(FilePath: "src/Program.cs", Title: "Optimize startup",
            Explanation: "Startup is slow", Scope: OpportunityScope.Architecture,
            RootCause: null, ImpactEstimate: null),
    ];

    [Theory]
    [MemberData(nameof(RoundTripTestCases))]
    public void Record_RoundTrips_ThroughJson(object original)
    {
        ArgumentNullException.ThrowIfNull(original);
        string json = JsonSerializer.Serialize(original, original.GetType());
        object? deserialized = JsonSerializer.Deserialize(json, original.GetType());

        _ = deserialized.Should().Be(original);
    }
}
