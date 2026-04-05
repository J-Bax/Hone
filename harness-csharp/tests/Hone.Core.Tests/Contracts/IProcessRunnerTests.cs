using System.Reflection;
using FluentAssertions;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.TestInfrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Hone.Core.Tests.Contracts;

public sealed class IProcessRunnerTests(ITestOutputHelper output) : HoneTestBase(output)
{
    [Fact]
    public void RunAsync_Method_IsPresent()
    {
        MethodInfo? method = typeof(IProcessRunner).GetMethod("RunAsync");

        _ = method.Should().NotBeNull("IProcessRunner must define RunAsync");
        _ = method!.ReturnType.Should().Be<Task<ProcessResult>>();
    }

    [Fact]
    public void RunAsync_HasExpectedParameters()
    {
        MethodInfo? method = typeof(IProcessRunner).GetMethod("RunAsync");
        ParameterInfo[] parameters = method!.GetParameters();

        _ = parameters.Should().HaveCount(5);
        _ = parameters[0].ParameterType.Should().Be<string>();
        _ = parameters[0].Name.Should().Be("executable");
        _ = parameters[1].ParameterType.Should().Be<IEnumerable<string>>();
        _ = parameters[1].Name.Should().Be("arguments");
        _ = parameters[2].ParameterType.Should().Be<string>();
        _ = parameters[2].Name.Should().Be("workingDirectory");
        _ = parameters[3].ParameterType.Should().Be<TimeSpan?>();
        _ = parameters[3].Name.Should().Be("timeout");
        _ = parameters[4].ParameterType.Should().Be<CancellationToken>();
        _ = parameters[4].Name.Should().Be("ct");
    }
}
