using System.Text.Json;
using Hone.Core.Models;

namespace Hone.TestInfrastructure.HarnessTesting;

/// <summary>
/// Loads deterministic harness-testing fixtures from <c>harness-csharp/test-fixtures/harness-testing</c>.
/// </summary>
public sealed class HarnessFixtureCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _rootPath;

    public HarnessFixtureCatalog(string rootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        _rootPath = rootPath;
    }

    public IReadOnlyList<Opportunity> LoadOpportunities(string fixtureName) =>
        LoadJson<IReadOnlyList<Opportunity>>(Path.Combine(_rootPath, "agent-responses", fixtureName));

    public MetricSet LoadMetricSet(string fixtureName) =>
        LoadJson<MetricSet>(Path.Combine(_rootPath, "k6-results", fixtureName));

    public string GetTargetFixture(string fixtureName)
    {
        string path = Path.Combine(_rootPath, "targets", fixtureName);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Harness target fixture not found: {path}");
        }

        return path;
    }

    private static T LoadJson<T>(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Harness fixture not found.", path);
        }

        using FileStream stream = File.OpenRead(path);
        T? value = JsonSerializer.Deserialize<T>(stream, JsonOptions);
        return value ?? throw new InvalidOperationException($"Harness fixture '{path}' could not be deserialized.");
    }
}
