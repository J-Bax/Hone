using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Xunit;

namespace Hone.Core.Tests.Models;

public sealed class OpportunityTests
{
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
}
