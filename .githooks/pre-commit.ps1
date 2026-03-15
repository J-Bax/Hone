# =============================================================================
# Git Pre-Commit Hook — PSScriptAnalyzer Lint
# =============================================================================
# Lints staged PowerShell files under harness/ and root *.ps1 files.
# Blocks commits when PSScriptAnalyzer finds issues from "blocking" rules.
# Formatting rules are reported as warnings but do not block.
#
# To install: git config core.hooksPath .githooks
# Or run:     Setup-DevEnvironment.ps1
# To bypass:  git commit --no-verify

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Console {
    param(
        [string]$Text = '',
        [System.ConsoleColor]$Color,
        [switch]$NoNewline
    )
    if ($PSBoundParameters.ContainsKey('Color')) {
        if ($NoNewline) {
            $Host.UI.Write($Color, $Host.UI.RawUI.BackgroundColor, $Text)
        } else {
            $Host.UI.WriteLine($Color, $Host.UI.RawUI.BackgroundColor, $Text)
        }
    } elseif ($NoNewline) {
        $Host.UI.Write($Text)
    } else {
        $Host.UI.WriteLine($Text)
    }
}

# Rules that block commits(correctness + quality). All other rules warn only.
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

# Check if PSScriptAnalyzer is available
if (-not (Get-Module -ListAvailable -Name PSScriptAnalyzer)) {
    Write-Warning 'PSScriptAnalyzer not installed — skipping pre-commit lint.'
    Write-Warning 'Run: Install-Module PSScriptAnalyzer -Force -Scope CurrentUser'
    exit 0
}

# Resolve paths
$repoRoot = git rev-parse --show-toplevel 2>$null
if (-not $repoRoot) {
    Write-Warning 'Could not determine repo root — skipping pre-commit lint.'
    exit 0
}
$repoRoot = $repoRoot.Trim()

$settingsPath = Join-Path $repoRoot '.PSScriptAnalyzerSettings.psd1'
if (-not (Test-Path $settingsPath)) {
    Write-Warning 'PSScriptAnalyzer settings not found — skipping pre-commit lint.'
    exit 0
}

# Get staged files (excluding deleted)
$stagedFiles = git diff --cached --name-only --diff-filter=d 2>$null
if (-not $stagedFiles) {
    exit 0
}

# Filter to PowerShell files in scope
$psExtensions = @('.ps1', '.psm1', '.psd1')
$filesToLint = @()

foreach ($file in $stagedFiles) {
    $ext = [System.IO.Path]::GetExtension($file)
    if ($ext -notin $psExtensions) { continue }

    # Only lint harness scripts and root .ps1 files
    $isHarness = $file -like 'harness/*' -or $file -like 'harness\*'
    $isRootPs1 = ($file -notlike '*/*' -and $file -notlike '*\*') -and $ext -eq '.ps1'
    if (-not $isHarness -and -not $isRootPs1) { continue }

    $fullPath = Join-Path $repoRoot $file
    if (Test-Path $fullPath) {
        $filesToLint += @{ Full = $fullPath; Relative = $file }
    }
}

if ($filesToLint.Count -eq 0) {
    exit 0
}

Write-Console "Linting $($filesToLint.Count) PowerShell file(s)..." -Color Cyan

Import-Module PSScriptAnalyzer -ErrorAction Stop

$errorCount = 0
$warningCount = 0

foreach ($entry in $filesToLint) {
    $results = $null
    try {
        $results = Invoke-ScriptAnalyzer -Path $entry.Full -Settings $settingsPath
    } catch {
        Write-Console "  [!] $($entry.Relative): PSScriptAnalyzer internal error — skipped" -Color Yellow
    }

    if ($results) {
        foreach ($r in $results) {
            $isBlocking = $r.RuleName -in $blockingRules

            if ($isBlocking) {
                Write-Console "  [X] $($entry.Relative):$($r.Line) $($r.RuleName) — $($r.Message)" -Color Red
                $errorCount++
            } else {
                Write-Console "  [!] $($entry.Relative):$($r.Line) $($r.RuleName) — $($r.Message)" -Color Yellow
                $warningCount++
            }
        }
    }
}

if ($errorCount -gt 0) {
    Write-Console '' -Color Red
    Write-Console "Lint FAILED: $errorCount error(s), $warningCount warning(s)" -Color Red
    Write-Console 'Fix errors before committing, or use: git commit --no-verify' -Color Red
    exit 1
}

if ($warningCount -gt 0) {
    Write-Console "Lint passed with $warningCount warning(s)" -Color Yellow
} else {
    Write-Console 'Lint passed.' -Color Green
}
exit 0
