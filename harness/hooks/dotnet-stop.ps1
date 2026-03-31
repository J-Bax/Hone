<#
.SYNOPSIS
    Shared hook: stops a .NET API process.

.DESCRIPTION
    Gracefully stops the API process that was started by dotnet-start.ps1.
    Expects the process to be passed via $Config._Process.

.PARAMETER TargetPath
    Root path of the target project.

.PARAMETER Config
    Harness configuration hashtable. Must include _Process (the Process object
    returned by dotnet-start.ps1).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$TargetPath,
    [Parameter(Mandatory)] [hashtable]$Config,
    [string]$BaseUrl,
    [string]$Experiment
)

# TargetPath, BaseUrl, Experiment accepted for contract conformance but not used.
$null = $TargetPath, $BaseUrl, $Experiment

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

$process = $Config._Process

if (-not $process) {
    $stopwatch.Stop()
    return [PSCustomObject]@{
        Success = $false
        Message = 'No process provided in $Config._Process'
        Duration = $stopwatch.Elapsed
        Artifacts = @()
    }
}

try {
    # dotnet run spawns the apphost (e.g. PublicApi.exe) as a child process.
    # Stop-Process only kills the targeted PID, so collect child PIDs first
    # to prevent orphaned processes from locking the executable on disk.
    $childPids = @()
    try {
        $cimChildren = Get-CimInstance Win32_Process -Filter "ParentProcessId = $($process.Id)" -ErrorAction SilentlyContinue
        $childPids = @($cimChildren | ForEach-Object { $_.ProcessId })
    } catch {
        Write-Verbose "Could not enumerate child processes: $_"
    }

    if (-not $process.HasExited) {
        Write-Verbose "Stopping API process (PID: $($process.Id))"
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
        $process.WaitForExit(5000) | Out-Null
        $message = "API process stopped (PID $($process.Id))"
    } else {
        $message = "API process had already exited (PID $($process.Id))"
    }

    # Kill any surviving child processes (apphost orphans)
    foreach ($childPid in $childPids) {
        try {
            $child = Get-Process -Id $childPid -ErrorAction SilentlyContinue
            if ($child -and -not $child.HasExited) {
                Write-Verbose "Stopping orphaned child process (PID: $childPid, Name: $($child.ProcessName))"
                Stop-Process -Id $childPid -Force -ErrorAction SilentlyContinue
                $message += "; child process stopped (PID $childPid)"
            }
        } catch {
            Write-Verbose "Child process $childPid already exited"
        }
    }

    $success = $true
} catch {
    $message = "Failed to stop API process: $_"
    $success = $false
}

$stopwatch.Stop()

return [PSCustomObject]@{
    Success = $success
    Message = $message
    Duration = $stopwatch.Elapsed
    Artifacts = @()
}
