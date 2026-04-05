<#
.SYNOPSIS
    Stops the dotnet-counters collector plugin.

.DESCRIPTION
    Wraps the existing Stop-DotnetCounters.ps1 script into the standard
    collector plugin interface. The inner handle (containing Process and
    OutputPath) is forwarded to the harness-level script which stops the
    dotnet-counters process and parses the CSV into structured JSON.

.PARAMETER Handle
    Handle hashtable returned by Start-Collector.ps1.

.OUTPUTS
    Hashtable with Success [bool] and ArtifactPaths [string[]].
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [hashtable]$Handle
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$innerHandle = $Handle.InnerHandle
$csvPath = $Handle.OutputPath

Write-Verbose "Delegating to Stop-DotnetCounters.ps1 for $csvPath"

$stopScript = Join-Path $PSScriptRoot '..\..\Stop-DotnetCounters.ps1'
$metrics = & $stopScript -CounterHandle $innerHandle

# The existing script writes a JSON file alongside the CSV
$jsonPath = [System.IO.Path]::ChangeExtension($csvPath, '.json')

$artifactPaths = @()
if (Test-Path $csvPath) { $artifactPaths += $csvPath }
if (Test-Path $jsonPath) { $artifactPaths += $jsonPath }

$success = $null -ne $metrics
if (-not $success) {
    Write-Information "dotnet-counters collection returned no metrics; artifacts may be incomplete."
}

return @{
    Success = $success
    ArtifactPaths = $artifactPaths
}
