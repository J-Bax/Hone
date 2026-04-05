namespace Hone.Measurement.DotnetCounters;

/// <summary>
/// Internal state tracked between <see cref="DotnetCountersCollector.StartAsync"/> and
/// <see cref="DotnetCountersCollector.StopAndParseAsync"/>.
/// </summary>
internal sealed record DotnetCountersHandle(string OutputPath, int ProcessId);
