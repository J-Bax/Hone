using FluentAssertions;

using Hone.Reporting.Console;
using Hone.TestInfrastructure;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Reporting.Tests.Console;

public sealed class AssessmentRendererTests(ITestOutputHelper output) : HoneTestBase(output)
{
    private static string RenderToString(AssessmentViewModel model)
    {
        using var stringWriter = new StringWriter();
        var writer = new PlainTextColorWriter(stringWriter);
        AssessmentRenderer.Render(model, writer);
        return stringWriter.ToString();
    }

    [Fact]
    public void Render_Compatible_ShowsGreenScore()
    {
        // Arrange
        var model = new AssessmentViewModel(
            TargetName: "MyApp",
            Overall: "compatible",
            Score: 85,
            Blockers: [],
            Warnings: [],
            ReadyItems:
            [
                new AssessmentReadyViewModel(Area: "build", Detail: "dotnet build succeeds"),
                new AssessmentReadyViewModel(Area: "tests", Detail: "All tests pass"),
            ],
            OnboardingSummary: "Ready for onboarding");

        // Act
        string result = RenderToString(model);
        Output.WriteLine(result);

        // Assert
        _ = result.Should().Contain("COMPATIBLE");
        _ = result.Should().Contain("85/100");
        _ = result.Should().Contain("MyApp");
        _ = result.Should().Contain("build");
        _ = result.Should().Contain("dotnet build succeeds");
    }

    [Fact]
    public void Render_Incompatible_ShowsRedBlockers()
    {
        // Arrange
        var model = new AssessmentViewModel(
            TargetName: "BrokenApp",
            Overall: "incompatible",
            Score: 15,
            Blockers:
            [
                new AssessmentFindingViewModel(
                    Area: "build",
                    Issue: "Build fails with errors",
                    Remediation: "Fix compilation errors"),
            ],
            Warnings: [],
            ReadyItems: [],
            OnboardingSummary: null);

        // Act
        string result = RenderToString(model);
        Output.WriteLine(result);

        // Assert
        _ = result.Should().Contain("INCOMPATIBLE");
        _ = result.Should().Contain("15/100");
        _ = result.Should().Contain("Blockers");
        _ = result.Should().Contain("Build fails with errors");
        _ = result.Should().Contain("Fix compilation errors");
    }

    [Fact]
    public void Render_Partial_ShowsWarnings()
    {
        // Arrange
        var model = new AssessmentViewModel(
            TargetName: "PartialApp",
            Overall: "partial",
            Score: 55,
            Blockers: [],
            Warnings:
            [
                new AssessmentFindingViewModel(
                    Area: "database",
                    Issue: "No reset strategy detected",
                    Remediation: "Add a database reset hook"),
            ],
            ReadyItems:
            [
                new AssessmentReadyViewModel(Area: "build", Detail: "builds OK"),
            ],
            OnboardingSummary: "Needs some work");

        // Act
        string result = RenderToString(model);
        Output.WriteLine(result);

        // Assert
        _ = result.Should().Contain("PARTIAL");
        _ = result.Should().Contain("55/100");
        _ = result.Should().Contain("Warnings");
        _ = result.Should().Contain("No reset strategy detected");
        _ = result.Should().Contain("Add a database reset hook");
        _ = result.Should().Contain("Next Steps");
        _ = result.Should().Contain("hone init");
    }

    private sealed class PlainTextColorWriter(TextWriter writer) : IConsoleColorWriter
    {
        public void Write(string text, ConsoleColor? color = null) => writer.Write(text);

        public void WriteLine(string text = "", ConsoleColor? color = null) => writer.WriteLine(text);
    }
}
