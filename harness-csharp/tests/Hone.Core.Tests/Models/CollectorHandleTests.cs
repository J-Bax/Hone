using System.Text.Json;
using FluentAssertions;
using Hone.Core.Models;
using Xunit;

namespace Hone.Core.Tests.Models;

public sealed class CollectorHandleTests
{
    [Fact]
    public void Handle_IsExcludedFromJson()
    {
        CollectorHandle original = new(Success: true, Handle: new object());

        string json = JsonSerializer.Serialize(original);
        _ = json.Should().NotContain("Handle");
    }
}
