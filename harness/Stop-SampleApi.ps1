<#
.SYNOPSIS
    Stops the sample API process.

.DESCRIPTION
    Gracefully stops the API process that was started by Start-SampleApi.ps1.

.PARAMETER Process
    The Process object returned by Start-SampleApi.ps1.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [System.Diagnostics.Process]$Process
)

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'measure' -Level 'info' -Message "Stopping API process (PID: $($Process.Id))"

try {
    if (-not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force -ErrorAction Stop
        $Process.WaitForExit(5000) | Out-Null

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'info' -Message 'API process stopped'
    } else {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'info' -Message 'API process had already exited'
    }
} catch {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'warning' -Message "Failed to stop API process: $_"
}
