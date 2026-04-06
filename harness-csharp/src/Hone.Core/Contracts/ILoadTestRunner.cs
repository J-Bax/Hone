namespace Hone.Core.Contracts;

/// <summary>
/// Generic load test execution abstraction.
/// </summary>
public interface ILoadTestRunner
{
    /// <summary>
    /// Runs a load test with the specified options and returns the result.
    /// </summary>
    public Task<LoadTestResult> RunAsync(LoadTestOptions options, CancellationToken ct = default);
}
