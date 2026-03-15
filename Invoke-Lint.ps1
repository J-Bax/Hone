<#
.SYNOPSIS
    Runs PSScriptAnalyzer on Hone harness PowerShell scripts.

.DESCRIPTION
    Lints PowerShell scripts (.ps1, .psm1, .psd1) in the Hone harness using
    PSScriptAnalyzer with the repository settings file. Can lint all files,
    only git-staged files, or a specific path.

.PARAMETER Path
    Path to lint. Defaults to the repository root (harness/ and root .ps1 files).

.PARAMETER ChangedOnly
    When specified, only lint files that are staged in git (git diff --cached).

.PARAMETER Fix
    When specified, apply auto-fixes where PSScriptAnalyzer supports them.

.PARAMETER SettingsPath
    Path to PSScriptAnalyzer settings file. Defaults to .PSScriptAnalyzerSettings.psd1.

.EXAMPLE
    ./Invoke-Lint.ps1
    Lints all harness PowerShell files.

.EXAMPLE
    ./Invoke-Lint.ps1 -ChangedOnly
    Lints only git-staged PowerShell files (used by pre-commit hook).

.EXAMPLE
    ./Invoke-Lint.ps1 -Fix
    Lints and auto-fixes where possible.

.EXAMPLE
    ./Invoke-Lint.ps1 -Path harness/Invoke-HoneLoop.ps1
    Lints a specific file.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Path,

    [switch]$ChangedOnly,

    [switch]$Fix,

    [string]$SettingsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve repo root (script location)
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition

# Default settings path
if (-not $SettingsPath) {
    $SettingsPath = Join-Path $repoRoot '.PSScriptAnalyzerSettings.psd1'
}

if (-not (Test-Path $SettingsPath)) {
    Write-Error "PSScriptAnalyzer settings not found at: $SettingsPath"
    exit 1
}

# Check PSScriptAnalyzer is available
if (-not (Get-Module -ListAvailable -Name PSScriptAnalyzer)) {
    Write-Warning @"
PSScriptAnalyzer is not installed. Install it with:
    Install-Module PSScriptAnalyzer -Force -Scope CurrentUser
Or run Setup-DevEnvironment.ps1 to install all dependencies.
"@
    exit 1
}

Import-Module PSScriptAnalyzer -ErrorAction Stop

# PowerShell file extensions to lint
$psExtensions = @('.ps1', '.psm1', '.psd1')

# Collect files to lint
$filesToLint = @()

if ($ChangedOnly) {
    # Get git-staged files
    $stagedFiles = git diff --cached --name-only --diff-filter=d 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'Not in a git repository or git not available. Falling back to full lint.'
        $ChangedOnly = $false
    } else {
        foreach ($file in $stagedFiles) {
            $ext = [System.IO.Path]::GetExtension($file)
            if ($ext -notin $psExtensions) { continue }

            # Only lint harness scripts and root .ps1 files (not sample-api)
            $isHarness = $file -like 'harness/*' -or $file -like 'harness\*'
            $isRootPs1 = ($file -notlike '*/*' -and $file -notlike '*\*') -and $ext -eq '.ps1'
            if (-not $isHarness -and -not $isRootPs1) { continue }

            $fullPath = Join-Path $repoRoot $file
            if (Test-Path $fullPath) {
                $filesToLint += $fullPath
            }
        }
    }
}

if (-not $ChangedOnly) {
    if ($Path) {
        $resolvedPath = Resolve-Path $Path -ErrorAction Stop
        if (Test-Path $resolvedPath -PathType Container) {
            $filesToLint = Get-ChildItem -Path $resolvedPath -Recurse -File |
                Where-Object { $_.Extension -in $psExtensions } |
                Select-Object -ExpandProperty FullName
        } else {
            $filesToLint = @($resolvedPath.Path)
        }
    } else {
        # Default: harness/ directory + root .ps1 files
        $harnessFiles = Get-ChildItem -Path (Join-Path $repoRoot 'harness') -Recurse -File |
            Where-Object { $_.Extension -in $psExtensions } |
            Select-Object -ExpandProperty FullName

        $rootPs1Files = Get-ChildItem -Path $repoRoot -File -Filter '*.ps1' |
            Select-Object -ExpandProperty FullName

        $filesToLint = @($harnessFiles) + @($rootPs1Files)
    }
}

if ($filesToLint.Count -eq 0) {
    Write-Information 'No PowerShell files to lint.' -InformationAction Continue
    exit 0
}

Write-Information "Linting $($filesToLint.Count) file(s)..." -InformationAction Continue

# Rules that should cause a non-zero exit code (same list as pre-commit hook)
$blockingRules = @(
    'PSAvoidUsingCmdletAliases'
    'PSAvoidDefaultValueSwitchParameter'
    'PSAvoidGlobalVars'
    'PSAvoidUsingEmptyCatchBlock'
    'PSMisleadingBacktick'
    'PSPossibleIncorrectComparisonWithNull'
    'PSReservedCmdletChar'
    'PSReservedParams'
    'PSAvoidUsingUsernameAndPasswordParams'
    'PSAvoidUsingConvertToSecureStringWithPlainText'
    'PSUseDeclaredVarsMoreThanAssignments'
)

# Run PSScriptAnalyzer
$allResults = @()

foreach ($file in $filesToLint) {
    $relPath = $file
    if ($file.StartsWith($repoRoot)) {
        $relPath = $file.Substring($repoRoot.Length).TrimStart('\', '/')
    }

    $analyzerParams = @{
        Path     = $file
        Settings = $SettingsPath
    }

    if ($Fix) {
        $analyzerParams['Fix'] = $true
    }

    $results = Invoke-ScriptAnalyzer @analyzerParams

    if ($results) {
        foreach ($r in $results) {
            $allResults += [PSCustomObject]@{
                File     = $relPath
                Line     = $r.Line
                Severity = $r.Severity
                Rule     = $r.RuleName
                Message  = $r.Message
            }
        }
    }
}

# Summarize results — blocking rules cause failure, everything else is a warning
$errors = @($allResults | Where-Object { $_.Rule -in $blockingRules })
$warnings = @($allResults | Where-Object { $_.Rule -notin $blockingRules })

if ($allResults.Count -gt 0) {
    Write-Information '' -InformationAction Continue

    # Group by file for readable output
    $grouped = $allResults | Group-Object File
    foreach ($group in $grouped) {
        Write-Information "  $($group.Name)" -InformationAction Continue
        foreach ($issue in $group.Group) {
            $isBlocking = $issue.Rule -in $blockingRules
            $icon = if ($isBlocking) { 'X' } else { '!' }
            Write-Information "    [$icon] Line $($issue.Line): $($issue.Rule) - $($issue.Message)" -InformationAction Continue
        }
    }

    Write-Information '' -InformationAction Continue
}

$summary = "Lint complete: $($filesToLint.Count) file(s), $($errors.Count) error(s), $($warnings.Count) warning(s)"
Write-Information $summary -InformationAction Continue

if ($errors.Count -gt 0) {
    Write-Information 'Lint FAILED — errors must be fixed before committing.' -InformationAction Continue
    exit 1
}

Write-Information 'Lint PASSED.' -InformationAction Continue
exit 0
