<#
.SYNOPSIS
    Sets up the development environment for Hone.

.DESCRIPTION
    Installs all required dependencies (via winget and dotnet tool) and verifies
    they are available. Run this script once after cloning the repository to
    ensure your machine is ready for the Hone harness.

    Requires an elevated (Administrator) terminal for winget installs.

.PARAMETER SkipWinget
    Skip winget package installs (useful if you already have the tools installed).

.PARAMETER Force
    Re-install packages even if they appear to be present.

.EXAMPLE
    .\Setup-DevEnvironment.ps1

.EXAMPLE
    .\Setup-DevEnvironment.ps1 -SkipWinget
#>
[CmdletBinding()]
param(
    [switch]$SkipWinget,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Helpers ─────────────────────────────────────────────────────────────────

function Write-Step {
    param([string]$Message)
    Write-Host "`n▸ $Message" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Message)
    Write-Host "  ✓ $Message" -ForegroundColor Green
}

function Write-Skip {
    param([string]$Message)
    Write-Host "  – $Message (skipped)" -ForegroundColor Yellow
}

function Write-Fail {
    param([string]$Message)
    Write-Host "  ✗ $Message" -ForegroundColor Red
}

function Test-CommandExists {
    param([string]$Command)
    $null -ne (Get-Command $Command -ErrorAction SilentlyContinue)
}

function Update-SessionPath {
    # Refresh PATH from the registry so newly-installed tools are visible
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machinePath;$userPath"
}

function Install-WingetPackage {
    param(
        [string]$PackageId,
        [string]$Name
    )
    if (-not (Test-CommandExists 'winget')) {
        Write-Fail "winget is not available. Install App Installer from the Microsoft Store."
        return $false
    }
    try {
        winget install --id $PackageId --exact --accept-source-agreements --accept-package-agreements --silent
        Write-Ok "$Name installed via winget"
        $script:packagesInstalled = $true
        return $true
    } catch {
        Write-Fail "Failed to install $Name via winget: $_"
        return $false
    }
}

# Track overall status
$allSucceeded = $true
$packagesInstalled = $false

# ── 1. PowerShell 7.2+ ─────────────────────────────────────────────────────

Write-Step "Checking PowerShell version"
$psVersion = $PSVersionTable.PSVersion
if ($psVersion.Major -ge 7 -and $psVersion.Minor -ge 2) {
    Write-Ok "PowerShell $psVersion"
} elseif ($psVersion.Major -ge 7) {
    Write-Host "  ⚠ PowerShell $psVersion detected — 7.2+ recommended" -ForegroundColor Yellow
} else {
    Write-Fail "PowerShell $psVersion detected — 7.2+ required"
    Write-Host "    Install: winget install Microsoft.PowerShell" -ForegroundColor Gray
    $allSucceeded = $false
}

# ── 2. .NET SDK 6.0 ────────────────────────────────────────────────────────

Write-Step "Checking .NET SDK"
if ((Test-CommandExists 'dotnet') -and -not $Force) {
    $dotnetVersion = & dotnet --version 2>$null
    Write-Ok ".NET SDK $dotnetVersion"
} else {
    if ($SkipWinget) { Write-Skip ".NET SDK" }
    else {
        $result = Install-WingetPackage -PackageId 'Microsoft.DotNet.SDK.6' -Name '.NET SDK 6.0'
        if (-not $result) { $allSucceeded = $false }
    }
}

# ── 3. SQL Server LocalDB ──────────────────────────────────────────────────

Write-Step "Checking SQL Server LocalDB"
if ((Test-CommandExists 'sqllocaldb') -and -not $Force) {
    try {
        $localdbInfo = & sqllocaldb info 2>$null
        Write-Ok "SQL Server LocalDB available (instances: $($localdbInfo -join ', '))"
    } catch {
        Write-Ok "sqllocaldb found but could not list instances"
    }
} else {
    if ($SkipWinget) { Write-Skip "SQL Server LocalDB" }
    else {
        # No dedicated LocalDB winget package; install SQL Server Express which includes LocalDB
        Write-Host "  ⚠ LocalDB not found. Installing SQL Server 2019 Express (includes LocalDB)..." -ForegroundColor Yellow
        $result = Install-WingetPackage -PackageId 'Microsoft.SQLServer.2019.Express' -Name 'SQL Server 2019 Express (includes LocalDB)'
        if (-not $result) {
            Write-Fail "Alternatively, download the LocalDB installer from: https://aka.ms/sqllocaldb-msi"
            $allSucceeded = $false
        }
    }
}

# ── 4. k6 ──────────────────────────────────────────────────────────────────

Write-Step "Checking k6"
if ((Test-CommandExists 'k6') -and -not $Force) {
    $k6Version = & k6 version 2>$null
    Write-Ok "k6: $k6Version"
} else {
    if ($SkipWinget) { Write-Skip "k6" }
    else {
        $result = Install-WingetPackage -PackageId 'GrafanaLabs.k6' -Name 'k6'
        if (-not $result) { $allSucceeded = $false }
    }
}

# ── 5. GitHub CLI ──────────────────────────────────────────────────────────

Write-Step "Checking GitHub CLI"
$ghMinVersion = [version]'2.17.0'   # Minimum for 'gh auth token' and modern PR features
if ((Test-CommandExists 'gh') -and -not $Force) {
    $ghVersionLine = & gh --version 2>$null | Select-Object -First 1
    if ($ghVersionLine -match '(\d+\.\d+\.\d+)') {
        $ghParsed = [version]$Matches[1]
        if ($ghParsed -lt $ghMinVersion) {
            Write-Fail "$ghVersionLine — version $ghMinVersion+ required"
            Write-Host "    Upgrade: winget upgrade --id GitHub.cli" -ForegroundColor Gray
            $allSucceeded = $false
        } else {
            Write-Ok "$ghVersionLine"
        }
    } else {
        Write-Ok "$ghVersionLine (could not parse version)"
    }
} else {
    if ($SkipWinget) { Write-Skip "GitHub CLI" }
    else {
        $result = Install-WingetPackage -PackageId 'GitHub.cli' -Name 'GitHub CLI'
        if (-not $result) { $allSucceeded = $false }
    }
}

# ── 6. GitHub Copilot CLI extension ────────────────────────────────────────

# Refresh PATH so tools installed above (e.g. gh) are visible in this session
if ($packagesInstalled) {
    Update-SessionPath
}

Write-Step "Checking GitHub Copilot CLI"
if (Test-CommandExists 'copilot') {
    $copilotVer = & copilot --version 2>$null | Select-Object -First 1
    Write-Ok "copilot CLI installed ($copilotVer)"
} else {
    Write-Fail "Standalone copilot CLI not found — install it from https://docs.github.com/copilot/how-tos/copilot-cli"
    $allSucceeded = $false
}

# ── 7. dotnet-counters global tool ─────────────────────────────────────────

Write-Step "Checking dotnet-counters"
# dotnet-counters must be pinned to a version whose runtime is actually installed.
# The sample API targets .NET 6, so we install the 6.0.x line to guarantee
# the tool can run without requiring a newer runtime (e.g. .NET 8/9).
$dotnetCountersVersionSpec = '6.0.*'

if ((Test-CommandExists 'dotnet-counters') -and -not $Force) {
    # The tool may be on PATH but unable to run if the required runtime is missing.
    # Invoke it and check for a real version string to confirm it works.
    $dcVersionOutput = & dotnet-counters --version 2>&1
    if ($LASTEXITCODE -eq 0 -and $dcVersionOutput -match '^\d+\.\d+') {
        Write-Ok "dotnet-counters $dcVersionOutput"
    } else {
        Write-Fail "dotnet-counters is installed but cannot run (likely targets a .NET runtime not present on this machine)."
        Write-Host "    Fix: dotnet tool uninstall --global dotnet-counters" -ForegroundColor Gray
        Write-Host "    Then: dotnet tool install --global dotnet-counters --version $dotnetCountersVersionSpec" -ForegroundColor Gray
        $allSucceeded = $false
    }
} else {
    if (Test-CommandExists 'dotnet') {
        try {
            & dotnet tool install --global dotnet-counters --version $dotnetCountersVersionSpec 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "exit code $LASTEXITCODE" }
            $dcInstalledVersion = & dotnet-counters --version 2>$null
            Write-Ok "dotnet-counters $dcInstalledVersion installed (pinned to $dotnetCountersVersionSpec)"
        } catch {
            Write-Fail "Failed to install dotnet-counters ($dotnetCountersVersionSpec). Run: dotnet tool install --global dotnet-counters --version $dotnetCountersVersionSpec"
            $allSucceeded = $false
        }
    } else {
        Write-Fail "Cannot install dotnet-counters — .NET SDK not found"
        $allSucceeded = $false
    }
}

# ── 8. PerfView (diagnostic profiling) ─────────────────────────────────────

Write-Step "Checking PerfView"
$perfViewDir = Join-Path $PSScriptRoot 'tools' 'PerfView'
$perfViewExe = Join-Path $perfViewDir 'PerfView.exe'

if ((Test-Path $perfViewExe) -and -not $Force) {
    Write-Ok "PerfView found at $perfViewExe"
} else {
    Write-Host "  Downloading PerfView from GitHub releases..." -ForegroundColor Gray
    try {
        if (-not (Test-Path $perfViewDir)) {
            New-Item -ItemType Directory -Path $perfViewDir -Force | Out-Null
        }

        # Fetch latest release asset URL from GitHub API
        $releaseUrl = 'https://api.github.com/repos/microsoft/perfview/releases/latest'
        $headers = @{ 'User-Agent' = 'Hone-Setup' }
        $release = Invoke-RestMethod -Uri $releaseUrl -Headers $headers -ErrorAction Stop
        $asset = $release.assets | Where-Object { $_.name -eq 'PerfView.exe' } | Select-Object -First 1

        if (-not $asset) {
            throw "Could not find PerfView.exe asset in the latest release"
        }

        $downloadUrl = $asset.browser_download_url
        Invoke-WebRequest -Uri $downloadUrl -OutFile $perfViewExe -UseBasicParsing -ErrorAction Stop
        Write-Ok "PerfView downloaded to $perfViewExe"
    } catch {
        Write-Fail "Failed to download PerfView: $_"
        Write-Host "    Manual download: https://github.com/microsoft/perfview/releases" -ForegroundColor Gray
        $allSucceeded = $false
    }
}

# ── 9. Initialize LocalDB instance ──────────────────────────────────────────

Write-Step "Ensuring LocalDB instance 'MSSQLLocalDB' is running"
if (Test-CommandExists 'sqllocaldb') {
    try {
        & sqllocaldb create MSSQLLocalDB 2>$null
        & sqllocaldb start MSSQLLocalDB 2>$null
        Write-Ok "MSSQLLocalDB instance is running"
    } catch {
        Write-Host "  ⚠ Could not start MSSQLLocalDB — check sqllocaldb manually" -ForegroundColor Yellow
    }
} else {
    Write-Skip "LocalDB instance (sqllocaldb not available)"
}

# ── 10. Initialize Git Submodules ────────────────────────────────────────────

Write-Step "Initializing git submodules"
if (Test-CommandExists 'git') {
    $repoRoot = $PSScriptRoot
    Push-Location $repoRoot
    try {
        git submodule update --init --recursive 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Ok "Git submodules initialized"
        } else {
            Write-Fail "git submodule update failed (exit code $LASTEXITCODE)"
            $allSucceeded = $false
        }
    } catch {
        Write-Fail "Failed to initialize submodules: $_"
        $allSucceeded = $false
    } finally {
        Pop-Location
    }
} else {
    Write-Fail "git is not available — cannot initialize submodules"
    $allSucceeded = $false
}

# ── 11. Restore NuGet packages ─────────────────────────────────────────────

Write-Step "Restoring NuGet packages"
if (Test-CommandExists 'dotnet') {
    $repoRoot = $PSScriptRoot
    $slnPath = Join-Path $repoRoot 'sample-api' 'SampleApi.sln'
    if (Test-Path $slnPath) {
        & dotnet restore $slnPath
        Write-Ok "NuGet packages restored"
    } else {
        Write-Fail "Solution not found at $slnPath"
        $allSucceeded = $false
    }
} else {
    Write-Skip "NuGet restore (.NET SDK not available)"
}

# ── 12. PSScriptAnalyzer (PowerShell linting) ────────────────────────────────

Write-Step "Checking PSScriptAnalyzer"
$psaModule = Get-Module -ListAvailable -Name PSScriptAnalyzer | Select-Object -First 1
if ($psaModule -and -not $Force) {
    Write-Ok "PSScriptAnalyzer $($psaModule.Version)"
} else {
    try {
        Install-Module PSScriptAnalyzer -Force -Scope CurrentUser -ErrorAction Stop
        $psaInstalled = Get-Module -ListAvailable -Name PSScriptAnalyzer | Select-Object -First 1
        Write-Ok "PSScriptAnalyzer $($psaInstalled.Version) installed"
    } catch {
        Write-Fail "Failed to install PSScriptAnalyzer: $_"
        Write-Host "    Run: Install-Module PSScriptAnalyzer -Force -Scope CurrentUser" -ForegroundColor Gray
        $allSucceeded = $false
    }
}

# ── 13. Git hooks ────────────────────────────────────────────────────────────

Write-Step "Configuring git hooks"
if (Test-CommandExists 'git') {
    $repoRoot = $PSScriptRoot
    $hooksDir = Join-Path $repoRoot '.githooks'
    if (Test-Path $hooksDir) {
        Push-Location $repoRoot
        try {
            git config core.hooksPath .githooks
            Write-Ok "Git hooks path set to .githooks/"
        } catch {
            Write-Fail "Failed to configure git hooks path: $_"
            $allSucceeded = $false
        } finally {
            Pop-Location
        }
    } else {
        Write-Skip "Git hooks (.githooks/ directory not found)"
    }
} else {
    Write-Skip "Git hooks (git not available)"
}

# ── Summary ─────────────────────────────────────────────────────────────────

Write-Host "`n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor DarkGray
if ($allSucceeded) {
    Write-Host "  Setup complete — all dependencies are ready!" -ForegroundColor Green
    Write-Host "  Next step: .\harness\Get-PerformanceBaseline.ps1" -ForegroundColor Gray
} else {
    Write-Host "  Setup finished with errors — review the output above." -ForegroundColor Yellow
    Write-Host "  Fix the failures and re-run: .\Setup-DevEnvironment.ps1" -ForegroundColor Gray
}
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━`n" -ForegroundColor DarkGray

# Remind about admin requirement for PerfView
if (Test-Path (Join-Path $PSScriptRoot 'tools' 'PerfView' 'PerfView.exe')) {
    Write-Host "  🔒 PerfView diagnostic profiling requires an elevated (Admin) terminal at runtime.`n" -ForegroundColor DarkYellow
}

# Remind user to refresh PATH if anything was installed
if (-not $SkipWinget -or $Force) {
    Write-Host "  💡 If any tools were just installed, restart your terminal to refresh PATH.`n" -ForegroundColor DarkYellow
}
