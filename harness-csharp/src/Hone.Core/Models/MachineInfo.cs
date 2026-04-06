namespace Hone.Core.Models;

/// <summary>
/// Describes the hardware and software environment of the machine.
/// </summary>
public sealed record MachineInfo(
    string? CpuName,
    int? CpuCores,
    decimal? TotalRamGB,
    string? OsVersion,
    string? DotnetVersion);
