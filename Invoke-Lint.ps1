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
$script:AnalyzerHostPath = (Get-Process -Id $PID).Path
$script:AnalyzerTimeoutMs = 30000

function Get-HoneScriptAnalyzerEnabledRuleName {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$SettingsPath
    )

    $settings = Import-PowerShellDataFile -Path $SettingsPath
    $excludedRuleNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($ruleName in @($settings.ExcludeRules)) {
        $null = $excludedRuleNames.Add([string]$ruleName)
    }

    $disabledRuleNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    if ($settings.ContainsKey('Rules') -and $settings.Rules -is [System.Collections.IDictionary]) {
        foreach ($entry in $settings.Rules.GetEnumerator()) {
            if ($entry.Value -is [System.Collections.IDictionary] -and
                $entry.Value.Contains('Enable') -and
                -not [bool]$entry.Value.Enable) {
                $null = $disabledRuleNames.Add([string]$entry.Key)
            }
        }
    }

    [string[]]$enabledRuleNames = Get-ScriptAnalyzerRule |
        Where-Object {
            -not $excludedRuleNames.Contains($_.RuleName) -and
            -not $disabledRuleNames.Contains($_.RuleName)
        } |
        Select-Object -ExpandProperty RuleName -Unique

    return $enabledRuleNames
}

function Invoke-HoneScriptAnalyzerChild {
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string]$SettingsPath,

        [string[]]$IncludeRule,

        [switch]$Fix
    )

    $escapedFilePath = $FilePath.Replace("'", "''")
    $escapedSettingsPath = $SettingsPath.Replace("'", "''")
    $jsonMarker = '__PSSA_JSON__'
    $fixLine = if ($Fix) { "`$params['Fix'] = `$true" } else { '' }
    $includeRuleLine = ''
    if ($IncludeRule -and $IncludeRule.Count -gt 0) {
        $ruleLiteral = ($IncludeRule | ForEach-Object { "'$($_.Replace("'", "''"))'" }) -join ', '
        $includeRuleLine = "`$params['IncludeRule'] = @($ruleLiteral)"
    }

    $childScript = @"
`$ErrorActionPreference = 'Stop'
Import-Module PSScriptAnalyzer -ErrorAction Stop
`$null = Get-ScriptAnalyzerRule
`$params = @{
    Path = '$escapedFilePath'
    Settings = '$escapedSettingsPath'
    ErrorAction = 'Stop'
}
$fixLine
$includeRuleLine
`$results = @(Invoke-ScriptAnalyzer @params)
`$payload = foreach (`$item in `$results) {
    [PSCustomObject]@{
        Line = `$item.Line
        Severity = [string]`$item.Severity
        RuleName = `$item.RuleName
        Message = `$item.Message
    }
}
Write-Output '$jsonMarker'
`$payload | ConvertTo-Json -Depth 4 -Compress
"@

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $script:AnalyzerHostPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $null = $startInfo.ArgumentList.Add('-NoProfile')
    $null = $startInfo.ArgumentList.Add('-NonInteractive')
    $null = $startInfo.ArgumentList.Add('-Command')
    $null = $startInfo.ArgumentList.Add($childScript)

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $process.Start() | Out-Null

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()

    if (-not $process.WaitForExit($script:AnalyzerTimeoutMs)) {
        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
        } catch {
            Write-Verbose "Failed to stop hung ScriptAnalyzer child process $($process.Id): $_"
        }

        throw "ScriptAnalyzer timed out after $($script:AnalyzerTimeoutMs / 1000) seconds."
    }

    $output = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $exitCode = $process.ExitCode
    $combinedOutput = @($output, $stderr) -join [Environment]::NewLine

    if ($exitCode -ne 0) {
        throw ($combinedOutput.Trim())
    }

    $markerIndex = $output.LastIndexOf($jsonMarker)
    if ($markerIndex -lt 0) {
        return @()
    }

    $jsonText = $output.Substring($markerIndex + $jsonMarker.Length).Trim()
    if (-not $jsonText) {
        return @()
    }

    $parsed = $jsonText | ConvertFrom-Json
    if ($parsed -is [System.Array]) {
        return @($parsed)
    }

    return @($parsed)
}

function Invoke-HoneScriptAnalyzerWithRetry {
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string]$SettingsPath,

        [string[]]$IncludeRule,

        [switch]$Fix
    )

    $lastErrorMessage = $null
    foreach ($attempt in 1..3) {
        try {
            return Invoke-HoneScriptAnalyzerChild -FilePath $FilePath -SettingsPath $SettingsPath -IncludeRule $IncludeRule -Fix:$Fix
        } catch {
            $lastErrorMessage = $_.Exception.Message
            if ($attempt -lt 3) {
                Start-Sleep -Milliseconds 200
            }
        }
    }

    throw $lastErrorMessage
}

function Invoke-HoneScriptAnalyzerIsolated {
    <#
    .SYNOPSIS
        Runs ScriptAnalyzer in a fresh child PowerShell process for one file.
    .DESCRIPTION
        PSScriptAnalyzer occasionally terminates the hosting process with
        unhandled exceptions on certain files. Running each file in an isolated
        child process keeps linting reliable while preserving the same rules.
    #>
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string]$SettingsPath,

        [switch]$Fix
    )

    try {
        return Invoke-HoneScriptAnalyzerWithRetry -FilePath $FilePath -SettingsPath $SettingsPath -Fix:$Fix
    } catch {
        $fullRunError = $_.Exception.Message
        $fallbackResults = @()
        $enabledRuleNames = Get-HoneScriptAnalyzerEnabledRuleName -SettingsPath $SettingsPath

        foreach ($ruleName in $enabledRuleNames) {
            try {
                $fallbackResults += Invoke-HoneScriptAnalyzerWithRetry -FilePath $FilePath -SettingsPath $SettingsPath -IncludeRule $ruleName -Fix:$Fix
            } catch {
                throw "Full analyzer run failed and fallback rule '$ruleName' also failed for '$FilePath'. Full run error: $fullRunError. Rule error: $($_.Exception.Message)"
            }
        }

        return $fallbackResults
    }
}

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
    'PSAvoidUsingWriteHost'
    'PSUseApprovedVerbs'
    'PSUseShouldProcessForStateChangingFunctions'
    'PSAvoidUsingInvokeExpression'
    'PSUseBOMForUnicodeEncodedFile'
    'PSUseSingularNouns'
    'PSAvoidUsingPositionalParameters'
    'PSAvoidAssignmentToAutomaticVariable'
    'PSAvoidUsingBrokenHashAlgorithms'
    'PSPossibleIncorrectUsageOfAssignmentOperator'
    'PSPossibleIncorrectUsageOfRedirectionOperator'
)

# Run PSScriptAnalyzer
$allResults = @()
$analyzerFailures = @()

foreach ($file in $filesToLint) {
    $relPath = $file
    if ($file.StartsWith($repoRoot)) {
        $relPath = $file.Substring($repoRoot.Length).TrimStart('\', '/')
    }

    $results = $null
    try {
        $results = Invoke-HoneScriptAnalyzerIsolated -FilePath $file -SettingsPath $SettingsPath -Fix:$Fix
    } catch {
        $analyzerFailures += [PSCustomObject]@{
            File = $relPath
            Message = $_.Exception.Message
        }
        Write-Warning "PSScriptAnalyzer internal error on ${relPath}: $($_.Exception.Message)"
    }

    if ($results) {
        foreach ($r in $results) {
            $allResults += [PSCustomObject]@{
                File = $relPath
                Line = $r.Line
                Severity = $r.Severity
                Rule = $r.RuleName
                Message = $r.Message
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

if ($analyzerFailures.Count -gt 0) {
    foreach ($failure in $analyzerFailures) {
        Write-Information "  $($failure.File)" -InformationAction Continue
        Write-Information "    [X] PSScriptAnalyzer internal error - $($failure.Message)" -InformationAction Continue
    }
    Write-Information '' -InformationAction Continue
}

$summary = "Lint complete: $($filesToLint.Count) file(s), $($errors.Count) error(s), $($warnings.Count) warning(s), $($analyzerFailures.Count) analyzer failure(s)"
Write-Information $summary -InformationAction Continue

if ($errors.Count -gt 0 -or $analyzerFailures.Count -gt 0) {
    Write-Information 'Lint FAILED — errors must be fixed before committing.' -InformationAction Continue
    exit 1
}

Write-Information 'Lint PASSED.' -InformationAction Continue
exit 0
