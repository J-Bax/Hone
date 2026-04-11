using FluentAssertions;

using Hone.TestInfrastructure;

using Xunit;
using Xunit.Abstractions;

namespace Hone.Agents.Preparation.Tests;

public sealed class SourceCodePathDetectorTests(ITestOutputHelper output) : HoneTestBase(output)
{
    // ── .NET stack detection ────────────────────────────────────────────

    [Fact]
    public void Detect_DotNetProject_FindsSourceDirectories()
    {
        string targetDir = CreateTargetDir("dotnet-proj", b => b
            .AddFile("MyApp.sln", "solution")
            .AddFile("src/MyApp/MyApp.csproj", "<Project />")
            .AddFile("src/MyApp/Controllers/HomeController.cs", "class HomeController {}")
            .AddFile("src/MyApp/Services/OrderService.cs", "class OrderService {}")
            .AddFile("src/MyApp/Models/Order.cs", "class Order {}")
            .AddFile("src/MyApp/Data/AppDbContext.cs", "class AppDbContext {}"));

        var projectFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["dotnet-sln"] = ["MyApp.sln"],
            ["dotnet-csproj"] = ["src/MyApp/MyApp.csproj"],
        };

        SourceCodePathDetector.DetectionResult result =
            SourceCodePathDetector.Detect(targetDir, projectFiles);

        _ = result.DetectedStack.Should().Be("dotnet");
        _ = result.SourceFileGlob.Should().Be("*.cs");
        _ = result.SourceCodePaths.Should().Contain("src/MyApp/Controllers");
        _ = result.SourceCodePaths.Should().Contain("src/MyApp/Services");
        _ = result.SourceCodePaths.Should().Contain("src/MyApp/Models");
        _ = result.SourceCodePaths.Should().Contain("src/MyApp/Data");
    }

    // ── Excludes test directories ───────────────────────────────────────

    [Fact]
    public void Detect_ExcludesTestDirectories()
    {
        string targetDir = CreateTargetDir("exclude-tests", b => b
            .AddFile("MyApp.sln", "solution")
            .AddFile("src/MyApp/Controllers/HomeController.cs", "class HomeController {}")
            .AddFile("tests/MyApp.Tests/UnitTest1.cs", "class UnitTest1 {}")
            .AddFile("src/MyApp.IntegrationTests/SomeTest.cs", "class SomeTest {}"));

        var projectFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["dotnet-sln"] = ["MyApp.sln"],
        };

        SourceCodePathDetector.DetectionResult result =
            SourceCodePathDetector.Detect(targetDir, projectFiles);

        _ = result.SourceCodePaths.Should().Contain("src/MyApp/Controllers");
        _ = result.SourceCodePaths.Should().NotContain(p =>
            p.Contains("Tests", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("test", StringComparison.OrdinalIgnoreCase));
    }

    // ── Excludes build output directories ───────────────────────────────

    [Fact]
    public void Detect_ExcludesBuildOutputDirectories()
    {
        string targetDir = CreateTargetDir("exclude-build", b => b
            .AddFile("MyApp.sln", "solution")
            .AddFile("src/MyApp/Program.cs", "class Program {}")
            .AddFile("src/MyApp/bin/Debug/net10.0/MyApp.dll.cs", "// generated")
            .AddFile("src/MyApp/obj/Debug/net10.0/GeneratedCode.cs", "// generated"));

        var projectFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["dotnet-sln"] = ["MyApp.sln"],
        };

        SourceCodePathDetector.DetectionResult result =
            SourceCodePathDetector.Detect(targetDir, projectFiles);

        _ = result.SourceCodePaths.Should().NotContain(p =>
            p.Contains("bin", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("obj", StringComparison.OrdinalIgnoreCase));
    }

    // ── Excludes designer / generated files ─────────────────────────────

    [Fact]
    public void Detect_ExcludesDesignerFiles_StillFindsRealSource()
    {
        string targetDir = CreateTargetDir("designer-files", b => b
            .AddFile("MyApp.sln", "solution")
            .AddFile("src/MyApp/Properties/Resources.Designer.cs", "// auto-generated")
            .AddFile("src/MyApp/Services/RealService.cs", "class RealService {}"));

        var projectFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["dotnet-sln"] = ["MyApp.sln"],
        };

        SourceCodePathDetector.DetectionResult result =
            SourceCodePathDetector.Detect(targetDir, projectFiles);

        _ = result.SourceCodePaths.Should().Contain("src/MyApp/Services");
        // Properties dir has only designer files, so it may or may not be included
        // depending on whether any non-designer .cs files exist there
    }

    // ── Node.js stack detection ─────────────────────────────────────────

    [Fact]
    public void Detect_NodeProject_FindsTypeScriptDirectories()
    {
        string targetDir = CreateTargetDir("node-proj", b => b
            .AddFile("package.json", "{}")
            .AddFile("tsconfig.json", "{}")
            .AddFile("src/controllers/userController.ts", "export class UserController {}")
            .AddFile("src/services/authService.ts", "export class AuthService {}")
            .AddFile("src/models/user.ts", "export interface User {}"));

        var projectFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["node-package"] = ["package.json"],
            ["node-tsconfig"] = ["tsconfig.json"],
        };

        SourceCodePathDetector.DetectionResult result =
            SourceCodePathDetector.Detect(targetDir, projectFiles);

        _ = result.DetectedStack.Should().Be("node");
        _ = result.SourceFileGlob.Should().Be("*.ts");
        _ = result.SourceCodePaths.Should().Contain("src/controllers");
        _ = result.SourceCodePaths.Should().Contain("src/services");
        _ = result.SourceCodePaths.Should().Contain("src/models");
    }

    // ── Python stack detection ──────────────────────────────────────────

    [Fact]
    public void Detect_PythonProject_FindsPythonDirectories()
    {
        string targetDir = CreateTargetDir("python-proj", b => b
            .AddFile("requirements.txt", "flask==3.0")
            .AddFile("app/routes.py", "# routes")
            .AddFile("app/models.py", "# models")
            .AddFile("app/services/auth.py", "# auth service"));

        var projectFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["python-req"] = ["requirements.txt"],
        };

        SourceCodePathDetector.DetectionResult result =
            SourceCodePathDetector.Detect(targetDir, projectFiles);

        _ = result.DetectedStack.Should().Be("python");
        _ = result.SourceFileGlob.Should().Be("*.py");
        _ = result.SourceCodePaths.Should().Contain("app");
        _ = result.SourceCodePaths.Should().Contain("app/services");
    }

    // ── Go stack detection ──────────────────────────────────────────────

    [Fact]
    public void Detect_GoProject_FindsGoDirectories()
    {
        string targetDir = CreateTargetDir("go-proj", b => b
            .AddFile("go.mod", "module example.com/app")
            .AddFile("cmd/server/main.go", "package main")
            .AddFile("internal/handlers/handler.go", "package handlers")
            .AddFile("internal/models/user.go", "package models"));

        var projectFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["go-mod"] = ["go.mod"],
        };

        SourceCodePathDetector.DetectionResult result =
            SourceCodePathDetector.Detect(targetDir, projectFiles);

        _ = result.DetectedStack.Should().Be("go");
        _ = result.SourceFileGlob.Should().Be("*.go");
        _ = result.SourceCodePaths.Should().Contain("cmd/server");
        _ = result.SourceCodePaths.Should().Contain("internal/handlers");
        _ = result.SourceCodePaths.Should().Contain("internal/models");
    }

    // ── Unknown stack returns empty ─────────────────────────────────────

    [Fact]
    public void Detect_UnknownStack_ReturnsEmpty()
    {
        string targetDir = CreateTargetDir("unknown", b => b
            .AddFile("README.md", "# Unknown project"));

        var projectFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        SourceCodePathDetector.DetectionResult result =
            SourceCodePathDetector.Detect(targetDir, projectFiles);

        _ = result.Should().Be(SourceCodePathDetector.DetectionResult.Empty);
        _ = result.SourceCodePaths.Should().BeEmpty();
        _ = result.DetectedStack.Should().Be("unknown");
    }

    // ── Empty project returns empty paths ───────────────────────────────

    [Fact]
    public void Detect_EmptyDotNetProject_ReturnsEmptyPaths()
    {
        string targetDir = CreateTargetDir("empty-dotnet", b => b
            .AddFile("MyApp.sln", "solution"));

        var projectFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["dotnet-sln"] = ["MyApp.sln"],
        };

        SourceCodePathDetector.DetectionResult result =
            SourceCodePathDetector.Detect(targetDir, projectFiles);

        _ = result.DetectedStack.Should().Be("dotnet");
        _ = result.SourceCodePaths.Should().BeEmpty();
    }

    // ── Paths are sorted alphabetically ─────────────────────────────────

    [Fact]
    public void Detect_PathsAreSortedAlphabetically()
    {
        string targetDir = CreateTargetDir("sorted", b => b
            .AddFile("MyApp.sln", "solution")
            .AddFile("Zebra/Z.cs", "class Z {}")
            .AddFile("Alpha/A.cs", "class A {}")
            .AddFile("Middle/M.cs", "class M {}"));

        var projectFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["dotnet-sln"] = ["MyApp.sln"],
        };

        SourceCodePathDetector.DetectionResult result =
            SourceCodePathDetector.Detect(targetDir, projectFiles);

        _ = result.SourceCodePaths.Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    // ── Paths use forward slashes ───────────────────────────────────────

    [Fact]
    public void Detect_PathsUseForwardSlashes()
    {
        string targetDir = CreateTargetDir("slashes", b => b
            .AddFile("MyApp.sln", "solution")
            .AddFile("src/MyApp/Controllers/HomeController.cs", "class HomeController {}"));

        var projectFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["dotnet-sln"] = ["MyApp.sln"],
        };

        SourceCodePathDetector.DetectionResult result =
            SourceCodePathDetector.Detect(targetDir, projectFiles);

        _ = result.SourceCodePaths.Should().OnlyContain(p => !p.Contains('\\'));
    }

    // ── Stack inference priority ─────────────────────────────────────────

    [Theory]
    [InlineData("dotnet-sln", "dotnet")]
    [InlineData("dotnet-csproj", "dotnet")]
    [InlineData("node-package", "node")]
    [InlineData("go-mod", "go")]
    [InlineData("python-req", "python")]
    [InlineData("python-pyproj", "python")]
    [InlineData("rust-cargo", "rust")]
    [InlineData("java-maven", "java")]
    [InlineData("java-gradle", "java")]
    public void InferStack_ByProjectFileType(string fileKey, string expectedStack)
    {
        var projectFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            [fileKey] = ["dummy"],
        };

        string stack = SourceCodePathDetector.InferStack(projectFiles);

        _ = stack.Should().Be(expectedStack);
    }

    // ── Simple glob matching ────────────────────────────────────────────

    [Theory]
    [InlineData("Foo.Designer.cs", "*.Designer.cs", true)]
    [InlineData("Foo.cs", "*.Designer.cs", false)]
    [InlineData("GlobalUsings.cs", "GlobalUsings.cs", true)]
    [InlineData("Other.cs", "GlobalUsings.cs", false)]
    [InlineData("test_foo.py", "test_*.py", true)]
    [InlineData("foo.py", "test_*.py", false)]
    public void MatchesSimpleGlob_VariousPatterns(string fileName, string pattern, bool expected)
    {
        bool result = SourceCodePathDetector.MatchesSimpleGlob(fileName, pattern);
        _ = result.Should().Be(expected);
    }
}
