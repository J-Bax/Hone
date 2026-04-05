namespace Hone.Core.Config;

/// <summary>
/// Configuration for .NET performance counter collection during scale tests.
/// </summary>
public sealed record DotnetCountersConfig(
    bool Enabled = true,
    IReadOnlyList<string>? Providers = null,
    int RefreshIntervalSeconds = 1)
{
    /// <summary>
    /// Gets the counter providers to collect.
    /// </summary>
    public IReadOnlyList<string> Providers { get; init; } =
        Providers ??
        [
            "System.Runtime",
            "Microsoft.AspNetCore.Hosting",
            "Microsoft.AspNetCore.Http.Connections",
            "System.Net.Http",
        ];
}
