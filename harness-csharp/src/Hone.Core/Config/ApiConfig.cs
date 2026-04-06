using System.Diagnostics.CodeAnalysis;

namespace Hone.Core.Config;

/// <summary>
/// Configuration for the target API under optimization.
/// </summary>
[SuppressMessage("Design", "CA1054:URI parameters should not be strings", Justification = "Config value loaded from YAML; parsed to Uri by consumers.")]
[SuppressMessage("Design", "CA1056:URI properties should not be strings", Justification = "Config value loaded from YAML; parsed to Uri by consumers.")]
public sealed record ApiConfig(
    string SolutionPath = "sample-api/SampleApi.sln",
    string ProjectPath = "sample-api/SampleApi",
    IReadOnlyList<string>? SourceCodePaths = null,
    string SourceFileGlob = "*.cs",
    string TestProjectPath = "sample-api/SampleApi.Tests",
    string BaseUrl = "http://localhost:0",
    string HealthEndpoint = "/health",
    string? GcEndpoint = "/diag/gc",
    int StartupTimeout = 90,
    string ResultsPath = "sample-api/.hone/results",
    string MetadataPath = "sample-api/.hone/results/metadata")
{
    /// <summary>
    /// Gets the subdirectories to scan for source code context.
    /// </summary>
    public IReadOnlyList<string> SourceCodePaths { get; init; } =
        SourceCodePaths ?? ["Controllers", "Data", "Models", "Pages"];
}
