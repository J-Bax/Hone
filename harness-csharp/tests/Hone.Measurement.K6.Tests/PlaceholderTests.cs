using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Measurement.K6.Tests;

public sealed class PlaceholderTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void Placeholder_ShouldPass() => Assert.True(true);
}
