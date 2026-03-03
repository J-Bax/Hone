<#
.SYNOPSIS
    Resets the sample API database to ensure clean state between iterations.

.DESCRIPTION
    Drops the HoneSampleDb database from LocalDB so that the next API startup
    recreates it from scratch with fresh seed data via EnsureCreated + SeedData.Initialize.
    This ensures every iteration starts with identical data for fair performance comparisons.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER Iteration
    Current iteration number for logging.
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [int]$Iteration = 0
)

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot 'config.psd1'
}

$config = Import-PowerShellDataFile -Path $ConfigPath

& (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
    -Phase 'measure' -Level 'info' `
    -Message 'Resetting database for clean iteration state' `
    -Iteration $Iteration

# ── Parse connection string from appsettings.json ───────────────────────────
$appSettingsPath = Join-Path $repoRoot $config.Api.ProjectPath 'appsettings.json'
$appSettings = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
$connectionString = $appSettings.ConnectionStrings.DefaultConnection

$serverMatch = [regex]::Match($connectionString, 'Server=([^;]+)')
$dbMatch     = [regex]::Match($connectionString, 'Database=([^;]+)')

if (-not $serverMatch.Success -or -not $dbMatch.Success) {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'error' `
        -Message "Could not parse connection string: $connectionString" `
        -Iteration $Iteration

    return [PSCustomObject]@{ Success = $false; Message = 'Failed to parse connection string' }
}

$server = $serverMatch.Groups[1].Value
$dbName = $dbMatch.Groups[1].Value

# ── Drop the database via sqlcmd ────────────────────────────────────────────
$dropQuery = @"
IF DB_ID('$dbName') IS NOT NULL
BEGIN
    ALTER DATABASE [$dbName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$dbName];
END
"@

try {
    # Verify sqlcmd is available
    $sqlcmdPath = Get-Command sqlcmd -ErrorAction SilentlyContinue
    if (-not $sqlcmdPath) {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'error' `
            -Message 'sqlcmd not found in PATH. Install SQL Server command-line tools or add sqlcmd to PATH.' `
            -Iteration $Iteration
        return [PSCustomObject]@{ Success = $false; Message = 'sqlcmd not found' }
    }

    $output = & sqlcmd -S $server -Q $dropQuery -b 2>&1
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'measure' -Level 'warning' `
            -Message "sqlcmd exited with code $exitCode — database may not have existed" `
            -Iteration $Iteration `
            -Data @{ output = ($output | Out-String) }
    }

    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'info' `
        -Message "Database '$dbName' dropped — will be recreated on next API startup" `
        -Iteration $Iteration

    return [PSCustomObject]@{ Success = $true; Database = $dbName }
}
catch {
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'measure' -Level 'error' `
        -Message "Failed to reset database: $_" `
        -Iteration $Iteration

    return [PSCustomObject]@{ Success = $false; Message = "Error: $_" }
}
