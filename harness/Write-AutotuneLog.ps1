<#
.SYNOPSIS
    Structured logging helper for the Autotune harness.

.DESCRIPTION
    Writes structured log entries as JSON-lines to a log file and optionally
    to the information stream. Each entry includes a timestamp, iteration,
    phase, level, message, and optional data payload.

.PARAMETER Phase
    The current agentic loop phase (build, verify, measure, compare, analyze, fix).

.PARAMETER Level
    Log level: verbose, info, warning, error.

.PARAMETER Message
    Human-readable log message.

.PARAMETER Data
    Optional hashtable of structured data to include in the log entry.

.PARAMETER Iteration
    Current iteration number.

.PARAMETER LogPath
    Path to the log file. Defaults to results/autotune.jsonl.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('build', 'verify', 'measure', 'compare', 'analyze', 'fix', 'loop', 'baseline', 'counters')]
    [string]$Phase,

    [Parameter(Mandatory)]
    [ValidateSet('verbose', 'info', 'warning', 'error')]
    [string]$Level,

    [Parameter(Mandatory)]
    [string]$Message,

    [hashtable]$Data = @{},

    [int]$Iteration = 0,

    [string]$LogPath
)

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $LogPath) {
    $LogPath = Join-Path $repoRoot 'results' 'autotune.jsonl'
}

# Ensure the output directory exists
$logDir = Split-Path -Parent $LogPath
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}

$entry = [ordered]@{
    timestamp = (Get-Date -Format 'o')
    iteration = $Iteration
    phase     = $Phase
    level     = $Level
    message   = $Message
    data      = $Data
}

$json = $entry | ConvertTo-Json -Compress -Depth 5

# Append to log file
$json | Out-File -FilePath $LogPath -Append -Encoding utf8

# Also write to the appropriate stream
switch ($Level) {
    'verbose' { Write-Verbose $Message }
    'info'    { Write-Information $Message -InformationAction Continue }
    'warning' { Write-Warning $Message }
    'error'   { Write-Error $Message }
}
