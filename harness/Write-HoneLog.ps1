<#
.SYNOPSIS
    Structured logging helper for the Hone harness.

.DESCRIPTION
    Writes structured log entries as JSON-lines to a log file and optionally
    to the information stream. Each entry includes a timestamp, experiment,
    phase, level, message, and optional data payload.

.PARAMETER Phase
    The current experiment phase (measure, analyze, experiment, verify, publish).

.PARAMETER Level
    Log level: verbose, info, warning, error.

.PARAMETER Message
    Human-readable log message.

.PARAMETER Data
    Optional hashtable of structured data to include in the log entry.

.PARAMETER Experiment
    Current experiment number.

.PARAMETER LogPath
    Path to the log file. Defaults to sample-api/results/hone.jsonl.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('measure', 'analyze', 'experiment', 'verify', 'publish', 'loop', 'baseline', 'counters', 'metadata')]
    [string]$Phase,

    [Parameter(Mandatory)]
    [ValidateSet('verbose', 'info', 'warning', 'error')]
    [string]$Level,

    [Parameter(Mandatory)]
    [string]$Message,

    [hashtable]$Data = @{},

    [int]$Experiment = 0,

    [string]$LogPath
)

$repoRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

# Cache the resolved log path to avoid reloading config on every call
if (-not $LogPath) {
    if (-not $script:_cachedLogPath) {
        $config = Get-HoneConfig
        $script:_cachedLogPath = Join-Path -Path $repoRoot -ChildPath $config.Api.ResultsPath 'hone.jsonl'
    }
    $LogPath = $script:_cachedLogPath
}

# Ensure the output directory exists
$logDir = Split-Path -Parent $LogPath
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}

$entry = [ordered]@{
    timestamp = (Get-Date -Format 'o')
    experiment = $Experiment
    phase = $Phase
    level = $Level
    message = $Message
    data = $Data
}

$json = $entry | ConvertTo-Json -Compress -Depth 5

# Script-scoped cache for log rotation config
if (-not $script:_cachedMaxLogSizeMB) {
    $rotConfig = Get-HoneConfig
    $script:_cachedMaxLogSizeMB = if ($rotConfig.Logging -and $rotConfig.Logging.MaxFileSizeMB) {
        $rotConfig.Logging.MaxFileSizeMB
    } else { 50 }
}
$maxSizeMB = $script:_cachedMaxLogSizeMB
if (Test-Path $LogPath) {
    $logFile = Get-Item $LogPath
    if ($logFile.Length -gt ($maxSizeMB * 1MB)) {
        $rotatedPath = "$LogPath.1"
        Move-Item -Path $LogPath -Destination $rotatedPath -Force
    }
}

# Append to log file
$json | Out-File -FilePath $LogPath -Append -Encoding utf8

# Also write to the appropriate stream (with timestamp prefix)
$ts = Get-Date -Format 'HH:mm:ss'
switch ($Level) {
    'verbose' { Write-Verbose "[$ts] $Message" }
    'info' { Write-Information "[$ts] $Message" -InformationAction Continue }
    'warning' { Write-Warning "[$ts] $Message" }
    'error' { Write-Warning "[$ts] [ERROR] $Message" }
}
