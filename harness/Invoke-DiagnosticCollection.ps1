<#
.SYNOPSIS
    Discovers and runs all enabled diagnostic collector plugins.

.DESCRIPTION
    Scans the collectors directory for plugin directories, loads their
    metadata from collector.psd1, starts all enabled collectors, and
    returns handles for later Stop / Export calls.

    Each collector must expose:
      - collector.psd1       (metadata: Name, RequiresAdmin, DefaultSettings)
      - Start-Collector.ps1  (params: ProcessId, OutputDir, Settings → handle)
      - Stop-Collector.ps1   (params: Handle → artifact paths)
      - Export-CollectorData.ps1  (params: ArtifactPaths, OutputDir, ProcessName, Settings → exported paths + summary)

.PARAMETER ProcessId
    PID of the running .NET API process to profile.

.PARAMETER OutputDir
    Root directory for all collector output (per-collector subdirs are created).

.PARAMETER Config
    Loaded harness config hashtable.

.PARAMETER Action
    The lifecycle action to perform: 'Start', 'Stop', or 'Export'.

.PARAMETER Handles
    (Stop/Export only) Hashtable of collector name → handle from Start.

.PARAMETER ArtifactMap
    (Export only) Hashtable of collector name → artifact paths from Stop.

.PARAMETER ProcessName
    (Export only) API process name for PerfView filtering.

.OUTPUTS
    Start  → @{ Success; Handles = @{ 'perfview-cpu' = $handle; ... } }
    Stop   → @{ Success; ArtifactMap = @{ 'perfview-cpu' = @($paths); ... } }
    Export → @{ Success; CollectorData = @{ 'perfview-cpu' = @{ ExportedPaths; Summary }; ... } }
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Start', 'Stop', 'Export')]
    [string]$Action,

    [int]$ProcessId,
    [string]$OutputDir,
    [hashtable]$Config,
    [hashtable]$Handles,
    [hashtable]$ArtifactMap,
    [string]$ProcessName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$diagnostics = $Config.Diagnostics

$collectorsPath = Join-Path $repoRoot $diagnostics.CollectorsPath
if (-not (Test-Path $collectorsPath)) {
    Write-Warning "Collectors directory not found: $collectorsPath"
    return @{ Success = $false }
}

# ── Discover enabled collectors ─────────────────────────────────────────────
function Get-EnabledCollectors {
    param([string]$Path, [hashtable]$Diag)

    $result = [System.Collections.Generic.List[hashtable]]::new()
    foreach ($dir in (Get-ChildItem -Path $Path -Directory)) {
        $metaPath = Join-Path $dir.FullName 'collector.psd1'
        if (-not (Test-Path $metaPath)) { continue }

        $meta = Import-PowerShellDataFile $metaPath
        $name = $meta.Name ?? $dir.Name
        $configSettings = if ($Diag.CollectorSettings.ContainsKey($name)) {
            $Diag.CollectorSettings[$name]
        } else { @{} }

        $enabled = if ($configSettings.ContainsKey('Enabled')) {
            $configSettings.Enabled
        } else { $true }

        if (-not $enabled) {
            Write-Verbose "Collector '$name' is disabled in config — skipping"
            continue
        }

        # Merge settings: config overrides → defaults
        $merged = @{}
        if ($meta.DefaultSettings) {
            foreach ($k in $meta.DefaultSettings.Keys) { $merged[$k] = $meta.DefaultSettings[$k] }
        }
        foreach ($k in $configSettings.Keys) {
            if ($k -ne 'Enabled') { $merged[$k] = $configSettings[$k] }
        }

        # Inject shared settings
        $merged['PerfViewExePath'] = $Diag.PerfViewExePath

        $result.Add(@{
            Name     = $name
            Dir      = $dir.FullName
            Meta     = $meta
            Settings = $merged
        })
    }
    return $result
}

$collectors = Get-EnabledCollectors -Path $collectorsPath -Diag $diagnostics

# ── Action: Start ───────────────────────────────────────────────────────────
if ($Action -eq 'Start') {
    if (-not $ProcessId) { throw 'ProcessId is required for Start action' }
    if (-not $OutputDir) { throw 'OutputDir is required for Start action' }

    $handles = @{}
    $allSuccess = $true

    foreach ($c in $collectors) {
        $collectorOutputDir = Join-Path $OutputDir $c.Name
        if (-not (Test-Path $collectorOutputDir)) {
            New-Item -ItemType Directory -Path $collectorOutputDir -Force | Out-Null
        }

        $startScript = Join-Path $c.Dir 'Start-Collector.ps1'
        if (-not (Test-Path $startScript)) {
            Write-Warning "Start-Collector.ps1 not found for '$($c.Name)'"
            $allSuccess = $false
            continue
        }

        Write-Information "  Starting collector: $($c.Name)" -InformationAction Continue
        $result = & $startScript -ProcessId $ProcessId -OutputDir $collectorOutputDir -Settings $c.Settings

        if ($result.Success) {
            $handles[$c.Name] = $result.Handle
        }
        else {
            $errMsg = if ($result.ContainsKey('Error')) { $result.Error } else { 'unknown error (no Error property returned)' }
            Write-Warning "  Collector '$($c.Name)' failed to start: $errMsg"
            $allSuccess = $false
        }
    }

    return @{ Success = $allSuccess; Handles = $handles }
}

# ── Action: Stop ────────────────────────────────────────────────────────────
if ($Action -eq 'Stop') {
    if (-not $Handles) { throw 'Handles is required for Stop action' }

    $artifactMap = @{}
    $allSuccess = $true

    foreach ($c in $collectors) {
        if (-not $Handles.ContainsKey($c.Name)) { continue }

        $stopScript = Join-Path $c.Dir 'Stop-Collector.ps1'
        if (-not (Test-Path $stopScript)) {
            Write-Warning "Stop-Collector.ps1 not found for '$($c.Name)'"
            $allSuccess = $false
            continue
        }

        Write-Information "  Stopping collector: $($c.Name)" -InformationAction Continue
        $result = & $stopScript -Handle $Handles[$c.Name]

        if ($result.Success) {
            $artifactMap[$c.Name] = $result.ArtifactPaths
        }
        else {
            Write-Warning "  Collector '$($c.Name)' failed to stop"
            $allSuccess = $false
        }
    }

    return @{ Success = $allSuccess; ArtifactMap = $artifactMap }
}

# ── Action: Export ──────────────────────────────────────────────────────────
if ($Action -eq 'Export') {
    if (-not $ArtifactMap) { throw 'ArtifactMap is required for Export action' }
    if (-not $OutputDir)   { throw 'OutputDir is required for Export action' }

    $collectorData = @{}
    $allSuccess = $true

    foreach ($c in $collectors) {
        if (-not $ArtifactMap.ContainsKey($c.Name)) { continue }

        $exportScript = Join-Path $c.Dir 'Export-CollectorData.ps1'
        if (-not (Test-Path $exportScript)) {
            Write-Warning "Export-CollectorData.ps1 not found for '$($c.Name)'"
            $allSuccess = $false
            continue
        }

        $exportDir = Join-Path $OutputDir $c.Name
        if (-not (Test-Path $exportDir)) {
            New-Item -ItemType Directory -Path $exportDir -Force | Out-Null
        }

        Write-Information "  Exporting collector data: $($c.Name)" -InformationAction Continue
        $result = & $exportScript `
            -ArtifactPaths $ArtifactMap[$c.Name] `
            -OutputDir $exportDir `
            -ProcessName ($ProcessName ?? 'dotnet') `
            -Settings $c.Settings

        if ($result.Success) {
            $collectorData[$c.Name] = @{
                ExportedPaths = $result.ExportedPaths
                Summary       = $result.Summary
            }
        }
        else {
            $errDetail = if ($result.PSObject.Properties['Summary']) { $result.Summary }
                         elseif ($result.PSObject.Properties['Error']) { $result.Error }
                         else { 'unknown error' }
            Write-Warning "  Export failed for '$($c.Name)': $errDetail"
            $allSuccess = $false
        }
    }

    return @{ Success = $allSuccess; CollectorData = $collectorData }
}
