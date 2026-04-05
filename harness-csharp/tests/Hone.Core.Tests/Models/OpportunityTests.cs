using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Models;

public sealed class OpportunityTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void RoundTrips_ThroughJson()
    {
        Opportunity original = new(
            FilePath: "src/Api/Controllers/OrderController.cs",
            Title: "Reduce N+1 queries in order listing",
            Explanation: "Each order triggers a separate DB query for customer data",
            Scope: OpportunityScope.Narrow,
            RootCause: "Missing eager loading",
            ImpactEstimate: "~30% latency reduction on /api/orders");

        string json = JsonSerializer.Serialize(original);
        Opportunity? deserialized = JsonSerializer.Deserialize<Opportunity>(json);

        _ = deserialized.Should().Be(original);
    }

    [Fact]
    public void Opportunity_ScopeEnum_NarrowAndArchitecture()
    {
        Opportunity narrow = new(
            FilePath: "src/Service.cs",
            Title: "Narrow fix",
            Explanation: "Small change",
            Scope: OpportunityScope.Narrow,
            RootCause: null,
            ImpactEstimate: null);

        Opportunity architecture = new(
            FilePath: "src/Service.cs",
            Title: "Architecture fix",
            Explanation: "Broad change",
            Scope: OpportunityScope.Architecture,
            RootCause: null,
            ImpactEstimate: null);

        string narrowJson = JsonSerializer.Serialize(narrow);
        string architectureJson = JsonSerializer.Serialize(architecture);

        _ = narrowJson.Should().Contain("\"Narrow\"");
        _ = architectureJson.Should().Contain("\"Architecture\"");

        Opportunity? narrowDeserialized = JsonSerializer.Deserialize<Opportunity>(narrowJson);
        Opportunity? architectureDeserialized = JsonSerializer.Deserialize<Opportunity>(architectureJson);

        _ = narrowDeserialized.Should().Be(narrow);
        _ = architectureDeserialized.Should().Be(architecture);
    }

    [Fact]
    public void RoundTrips_WithNullOptionalFields()
    {
        Opportunity original = new(
            FilePath: "src/Program.cs",
            Title: "Optimize startup",
            Explanation: "Startup is slow",
            Scope: OpportunityScope.Architecture,
            RootCause: null,
            ImpactEstimate: null);

        string json = JsonSerializer.Serialize(original);
        Opportunity? deserialized = JsonSerializer.Deserialize<Opportunity>(json);

        _ = deserialized.Should().Be(original);
    }
}
