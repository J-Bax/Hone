<#
.SYNOPSIS
    Starts the dotnet-counters collector plugin.

.DESCRIPTION
    Wraps the existing Start-DotnetCounters.ps1 script into the standard
    collector plugin interface. Delegates all collection logic to the
    harness-level script and maps its return value to the plugin contract.

.PARAMETER ProcessId
    PID of the running .NET API process to monitor.

.PARAMETER OutputDir
    Directory where output artifacts will be written.

.PARAMETER Settings
    Merged collector settings hashtable.

.OUTPUTS
    Hashtable with Success [bool] and Handle [hashtable].
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$ProcessId,

    [Parameter(Mandatory)]
    [string]$OutputDir,

    [hashtable]$Settings = @{}
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$outputPath = Join-Path $OutputDir 'dotnet-counters.csv'

# Build parameters for the existing harness script
$startScript = Join-Path $PSScriptRoot '..\..\Start-DotnetCounters.ps1'
$startParams = @{
    ProcessId  = $ProcessId
    OutputPath = $outputPath
}

if ($Settings.ContainsKey('ConfigPath') -and $Settings.ConfigPath) {
    $startParams['ConfigPath'] = $Settings.ConfigPath
}

Write-Verbose "Delegating to Start-DotnetCounters.ps1 for PID $ProcessId → $outputPath"

$result = & $startScript @startParams

if ($result.Success) {
    return @{
        Success = $true
        Handle  = @{
            InnerHandle = $result
            OutputPath  = $outputPath
        }
    }
}
else {
    return @{
        Success = $false
        Error   = "dotnet-counters failed to start (exit code or missing tool). Check dotnet tool install --global dotnet-counters."
    }
}
