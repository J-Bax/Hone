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

function Get-TargetApiProcessCandidate {
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [System.Diagnostics.Process]$TrackedProcess,
        [string]$ProjectPath
    )

    $allProcesses = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
    if ($allProcesses.Count -eq 0) {
        return @()
    }

    $trackedProcessId = if ($TrackedProcess) { [int]$TrackedProcess.Id } else { $null }
    $normalizedProjectPath = if ($ProjectPath) { [System.IO.Path]::GetFullPath($ProjectPath).TrimEnd('\') } else { $null }
    $binPath = if ($normalizedProjectPath) { Join-Path $normalizedProjectPath 'bin' } else { $null }
    $seen = @{}
    $candidates = [System.Collections.Generic.List[object]]::new()

    foreach ($proc in $allProcesses) {
        $processId = [int]$proc.ProcessId
        $matchesTracked = ($trackedProcessId -and $processId -eq $trackedProcessId)

        $matchesProject = $false
        $executablePath = $proc.ExecutablePath
        if ($binPath -and $executablePath) {
            try {
                $normalizedExePath = [System.IO.Path]::GetFullPath($executablePath)
            } catch {
                $normalizedExePath = $executablePath
            }

            if ($normalizedExePath.StartsWith($binPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                $matchesProject = $true
            }
        }

        if (-not $matchesProject -and $normalizedProjectPath -and $proc.CommandLine) {
            $matchesProject = $proc.CommandLine.IndexOf($normalizedProjectPath, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        }

        if (-not ($matchesTracked -or $matchesProject)) {
            continue
        }

        if (-not $seen.ContainsKey($processId)) {
            $seen[$processId] = $true
            $candidates.Add([PSCustomObject]@{
                    ProcessId = $processId
                    Name = $proc.Name
                    ParentProcessId = [int]$proc.ParentProcessId
                })
        }
    }

    if ($candidates.Count -gt 0) {
        $rootIds = @($candidates | ForEach-Object { $_.ProcessId })
        foreach ($proc in $allProcesses) {
            $processId = [int]$proc.ProcessId
            if ($seen.ContainsKey($processId)) {
                continue
            }

            if ($rootIds -contains [int]$proc.ParentProcessId) {
                $seen[$processId] = $true
                $candidates.Add([PSCustomObject]@{
                        ProcessId = $processId
                        Name = $proc.Name
                        ParentProcessId = [int]$proc.ParentProcessId
                    })
            }
        }
    }

    return @($candidates | Sort-Object -Property ProcessId -Unique)
}

$process = $Config._Process
$projectPath = if ($Config.Api -and $Config.Api.ProjectPath) {
    Join-Path $TargetPath $Config.Api.ProjectPath
} else {
    $null
}
$stopTargets = @(Get-TargetApiProcessCandidate -TrackedProcess $process -ProjectPath $projectPath)

if ($stopTargets.Count -eq 0) {
    $stopwatch.Stop()
    return [PSCustomObject]@{
        Success = $true
        Message = 'No running target API processes found'
        Duration = $stopwatch.Elapsed
        Artifacts = @()
    }
}

$stopped = [System.Collections.Generic.List[string]]::new()
$failures = [System.Collections.Generic.List[string]]::new()

foreach ($stopTarget in $stopTargets) {
    try {
        $liveProcess = Get-Process -Id $stopTarget.ProcessId -ErrorAction SilentlyContinue
        if (-not $liveProcess -or $liveProcess.HasExited) {
            continue
        }

        Write-Verbose "Stopping target API process (PID: $($stopTarget.ProcessId), Name: $($stopTarget.Name))"
        Stop-Process -Id $stopTarget.ProcessId -Force -ErrorAction Stop
        $liveProcess.WaitForExit(5000) | Out-Null
        $stopped.Add("$($stopTarget.Name) ($($stopTarget.ProcessId))")
    } catch {
        $failures.Add("PID $($stopTarget.ProcessId): $_")
    }
}

if ($failures.Count -gt 0) {
    $message = "Failed to stop some target API processes: $($failures -join '; ')"
    $success = $false
} elseif ($stopped.Count -gt 0) {
    $message = "Stopped target API processes: $($stopped -join ', ')"
    $success = $true
} else {
    $message = 'No running target API processes found'
    $success = $true
}

$stopwatch.Stop()

return [PSCustomObject]@{
    Success = $success
    Message = $message
    Duration = $stopwatch.Elapsed
    Artifacts = @()
}
