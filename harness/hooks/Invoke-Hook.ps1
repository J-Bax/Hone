<#
.SYNOPSIS
    Generic hook dispatcher for the Hone harness.

.DESCRIPTION
    Takes a resolved hook definition and executes it according to its type:
    `Script`, `Command`, `Http`, or `Skip`.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [hashtable]$Hook,
    [Parameter(Mandatory)] [string]$TargetPath,
    [Parameter(Mandatory)] [hashtable]$Config,
    [string]$BaseUrl,
    [string]$Experiment
)

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

switch ($Hook.Type) {
    'Script' {
        return & $Hook.Path `
            -TargetPath $TargetPath `
            -Config $Config `
            -BaseUrl $BaseUrl `
            -Experiment $Experiment
    }

    'Command' {
        try {
            $sb = [scriptblock]::Create($Hook.Value)
            $output = & $sb 2>&1
            $exitCode = $LASTEXITCODE
            $stopwatch.Stop()

            return [PSCustomObject]@{
                Success = ($null -eq $exitCode -or $exitCode -eq 0)
                Message = if ($exitCode -eq 0 -or $null -eq $exitCode) { 'Command completed' } else { "Command failed (exit code $exitCode)" }
                Duration = $stopwatch.Elapsed
                Artifacts = @()
                ExitCode = $exitCode
                Output = ($output | Out-String)
            }
        } catch {
            $stopwatch.Stop()
            return [PSCustomObject]@{
                Success = $false
                Message = "Command error: $_"
                Duration = $stopwatch.Elapsed
                Artifacts = @()
            }
        }
    }

    'Http' {
        $method = if ($Hook.Method) { $Hook.Method } else { 'GET' }
        $uri = "$BaseUrl$($Hook.Path)"

        try {
            $response = Invoke-RestMethod -Method $method -Uri $uri -ErrorAction Stop
            $stopwatch.Stop()

            return [PSCustomObject]@{
                Success = $true
                Message = "HTTP $method $uri succeeded"
                Duration = $stopwatch.Elapsed
                Artifacts = @()
                Response = $response
            }
        } catch {
            $stopwatch.Stop()
            return [PSCustomObject]@{
                Success = $false
                Message = "HTTP $method $uri failed: $_"
                Duration = $stopwatch.Elapsed
                Artifacts = @()
            }
        }
    }

    'Skip' {
        return [PSCustomObject]@{
            Success = $true
            Message = 'Skipped'
            Duration = [timespan]::Zero
            Artifacts = @()
        }
    }

    default {
        $stopwatch.Stop()
        return [PSCustomObject]@{
            Success = $false
            Message = "Unknown hook type: $($Hook.Type)"
            Duration = $stopwatch.Elapsed
            Artifacts = @()
        }
    }
}
