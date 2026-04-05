<#
.SYNOPSIS
    Triggers server-side GC and sleeps for a cooldown period.

.DESCRIPTION
    Shared helper that encapsulates the cooldown pattern used between scale test
    runs and scenarios: trigger the GC diagnostic endpoint, log the cooldown,
    and sleep for the configured (or default) number of seconds.

.PARAMETER BaseUrl
    The API base URL (e.g. http://localhost:5000).

.PARAMETER GcEndpoint
    Relative path to the GC diagnostic endpoint (e.g. /diag/gc). If empty or
    null, the GC call is skipped.

.PARAMETER CooldownSeconds
    Number of seconds to sleep. Defaults to 3.

.PARAMETER Phase
    Logging phase label forwarded to Write-HoneLog. Defaults to 'measure'.

.PARAMETER Experiment
    Current experiment number for logging.

.PARAMETER Reason
    Short description included in the log message (e.g. 'between runs',
    'after warmup'). Defaults to 'cooldown'.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BaseUrl,

    [string]$GcEndpoint,

    [int]$CooldownSeconds = 3,

    [string]$Phase = 'measure',

    [int]$Experiment = 0,

    [string]$Reason = 'cooldown'
)

# Trigger server-side GC so heap pressure doesn't bleed over
if ($GcEndpoint) {
    try {
        Invoke-RestMethod -Uri "$BaseUrl$GcEndpoint" -Method Post -TimeoutSec 5 -ErrorAction Stop | Out-Null
    } catch {
        Write-Verbose "GC endpoint not available — skipping forced GC"
    }
}

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase $Phase -Level 'info' `
    -Message "Cooldown ${CooldownSeconds}s — $Reason" `
    -Experiment $Experiment

Start-Sleep -Seconds $CooldownSeconds
