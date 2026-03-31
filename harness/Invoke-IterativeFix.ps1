<#
.SYNOPSIS
    Runs the Phase 3 iterative fixer loop for a single experiment.

.DESCRIPTION
    Orchestrates the fix/apply/build/test cycle for one optimization experiment.
    When Fixer.MaxAttempts is greater than 1, failed build/test attempts feed
    concrete error output back into the fixer before the experiment is finally
    rejected. Per-attempt artifacts are written under
    experiment-N\iterations\attempt-M\ and summarized in iteration-log.json.

.PARAMETER FilePath
    Target file path from the analysis queue item.

.PARAMETER Explanation
    Optimization explanation from the analysis queue item.

.PARAMETER RootCauseDocument
    Optional RCA markdown to include in fixer prompts.

.PARAMETER Experiment
    Current experiment number.

.PARAMETER BaseBranch
    Branch to fork from on the first attempt.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER TargetDir
    Root directory of the target project.

.PARAMETER TargetName
    Human-readable target name used when normalizing agent-suggested paths.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$FilePath,

    [Parameter(Mandatory)]
    [string]$Explanation,

    [string]$RootCauseDocument,

    [Parameter(Mandatory)]
    [int]$Experiment,

    [Parameter(Mandatory)]
    [string]$BaseBranch,

    [string]$ConfigPath,

    [Parameter(Mandatory)]
    [string]$TargetDir,

    [string]$TargetName
)

$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

function ConvertTo-IterationRelativePath {
    param(
        [string]$Path,
        [string]$Root
    )

    if (-not $Path) {
        return $null
    }

    $resolvedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if ($resolvedPath.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $resolvedPath.Substring($resolvedRoot.Length).TrimStart('\').Replace('\', '/')
    }

    return $Path.Replace('\', '/')
}

function Limit-IterationErrorText {
    param(
        [string]$Text,
        [int]$MaxLength = 4000
    )

    if (-not $Text) {
        return $null
    }

    $trimmed = $Text.Trim()
    if ($trimmed.Length -le $MaxLength) {
        return $trimmed
    }

    return $trimmed.Substring(0, $MaxLength) + "`n... [truncated]"
}

function Format-RetryContext {
    param(
        [int]$Attempt,
        [string]$Stage,
        [string]$ErrorOutput
    )

    $safeStage = if ($Stage) { $Stage } else { 'unknown' }
    $safeOutput = if ($ErrorOutput) { $ErrorOutput.Trim() } else { 'No error output was captured.' }

    return @"
Attempt $Attempt failed at the $safeStage stage.

$safeOutput
"@
}

function Resolve-ExperimentTargetFile {
    param(
        [string]$CandidatePath,
        [string]$ProjectRoot,
        [hashtable]$MergedConfig,
        [string]$DisplayTargetName,
        [int]$ExperimentNumber
    )

    $targetFile = $CandidatePath.Trim()
    if ($DisplayTargetName -and $targetFile -match "^$([regex]::Escape($DisplayTargetName))[\\/]") {
        $targetFile = $targetFile.Substring($DisplayTargetName.Length + 1)
    }

    $fullTargetPath = Join-Path $ProjectRoot $targetFile
    if (Test-Path (Split-Path $fullTargetPath -Parent)) {
        return [PSCustomObject]@{
            Success = $true
            TargetFile = $targetFile
            FullTargetPath = $fullTargetPath
        }
    }

    $projectDir = Join-Path $ProjectRoot $MergedConfig.Api.ProjectPath
    $fileName = Split-Path $targetFile -Leaf
    $candidates = @(Get-ChildItem -Path $projectDir -Filter $fileName -Recurse -File -ErrorAction SilentlyContinue)
    if ($candidates.Count -eq 1) {
        $correctedPath = $candidates[0].FullName.Substring($ProjectRoot.Length + 1).Replace('\', '/')
        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'experiment' -Level 'warning' `
            -Message "Path corrected: '$targetFile' -> '$correctedPath'" `
            -Experiment $ExperimentNumber

        return [PSCustomObject]@{
            Success = $true
            TargetFile = $correctedPath
            FullTargetPath = $candidates[0].FullName
        }
    }

    return [PSCustomObject]@{
        Success = $false
        TargetFile = $targetFile
        FullTargetPath = $fullTargetPath
    }
}

function Get-LatestCommitChangedFileList {
    param([string]$RepositoryRoot)

    Push-Location $RepositoryRoot
    try {
        $changed = @(git diff --name-only HEAD~1 HEAD 2>$null)
        if ($LASTEXITCODE -ne 0) {
            return @()
        }

        return @($changed | Where-Object { $_ })
    } finally {
        Pop-Location
    }
}

function Get-LatestCommitDiffLineCount {
    param([string]$RepositoryRoot)

    Push-Location $RepositoryRoot
    try {
        $numstat = @(git diff --numstat HEAD~1 HEAD 2>$null)
        if ($LASTEXITCODE -ne 0) {
            return 0
        }

        $total = 0
        foreach ($line in $numstat) {
            if ($line -match '^\s*(\d+|-)\s+(\d+|-)\s+(.+)$') {
                if ($Matches[1] -ne '-') {
                    $total += [int]$Matches[1]
                }

                if ($Matches[2] -ne '-') {
                    $total += [int]$Matches[2]
                }
            }
        }

        return $total
    } finally {
        Pop-Location
    }
}

function Get-TestGuardRootList {
    param([hashtable]$MergedConfig)

    $guardRoots = @()
    $projectFileExtensions = @('.csproj', '.fsproj', '.vbproj', '.sln')

    if ($MergedConfig.Api.ContainsKey('TestProjectPaths') -and $MergedConfig.Api.TestProjectPaths) {
        $guardRoots += @($MergedConfig.Api.TestProjectPaths)
    } elseif ($MergedConfig.Api.ContainsKey('TestProjectPath') -and $MergedConfig.Api.TestProjectPath) {
        $candidate = [string]$MergedConfig.Api.TestProjectPath
        $normalizedCandidate = $candidate -replace '/', '\'
        if ($normalizedCandidate -match '(?i)(^|[\\/])tests?([\\/]|$)|\.tests([\\/]|$)') {
            $extension = [System.IO.Path]::GetExtension($normalizedCandidate)
            if ($extension -and ($extension.ToLowerInvariant() -in $projectFileExtensions)) {
                $parent = Split-Path -Path $candidate -Parent
                if ($parent) {
                    $guardRoots += $parent
                } else {
                    $guardRoots += $candidate
                }
            } else {
                $guardRoots += $candidate
            }
        }
    }

    $normalized = @()
    foreach ($root in $guardRoots) {
        if (-not $root) {
            continue
        }

        $value = ($root -replace '/', '\').Trim().TrimStart('.').TrimStart('\').TrimEnd('\')
        if ($value) {
            $normalized += $value
        }
    }

    return @($normalized | Select-Object -Unique)
}

function Get-ChangedTestFileList {
    param(
        [string[]]$ChangedFiles,
        [string[]]$GuardRoots
    )

    if (-not $ChangedFiles -or -not $GuardRoots) {
        return @()
    }

    $matchedFiles = @()
    foreach ($file in $ChangedFiles) {
        $normalizedFile = ($file -replace '/', '\').TrimStart('.').TrimStart('\')
        foreach ($root in $GuardRoots) {
            $normalizedRoot = ($root -replace '/', '\').Trim().TrimStart('.').TrimStart('\').TrimEnd('\')
            if (-not $normalizedRoot) {
                continue
            }

            if ($normalizedFile.Equals($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
                $normalizedFile.StartsWith("$normalizedRoot\", [System.StringComparison]::OrdinalIgnoreCase)) {
                $matchedFiles += $file
                break
            }
        }
    }

    return @($matchedFiles | Select-Object -Unique)
}

$config = Get-HoneConfig -ConfigPath $ConfigPath
$targetConfigPath = Join-Path -Path $TargetDir -ChildPath '.hone' -AdditionalChildPath 'config.psd1'
if (Test-Path $targetConfigPath) {
    $targetCfg = Import-PowerShellDataFile -Path $targetConfigPath
    $config = Merge-HoneConfig -Engine $config -Target $targetCfg
}

$resultsDir = Join-Path -Path $TargetDir -ChildPath $config.Api.ResultsPath
$experimentDir = Join-Path -Path $resultsDir -ChildPath "experiment-$Experiment"
if (-not (Test-Path $experimentDir)) {
    New-Item -ItemType Directory -Path $experimentDir -Force | Out-Null
}

$iterationLogPath = Join-Path -Path $experimentDir -ChildPath 'iteration-log.json'
$iterationEntries = [System.Collections.Generic.List[object]]::new()
$fixerConfig = if ($config.ContainsKey('Fixer') -and $config.Fixer) { $config.Fixer } else { @{} }
$maxAttempts = if ($fixerConfig.ContainsKey('MaxAttempts') -and $fixerConfig.MaxAttempts) { [int]$fixerConfig.MaxAttempts } else { 1 }
$maxAttempts = [Math]::Max($maxAttempts, 1)
$diffGrowthFactor = if ($fixerConfig.ContainsKey('MaxDiffGrowthFactor') -and $null -ne $fixerConfig.MaxDiffGrowthFactor) {
    [double]$fixerConfig.MaxDiffGrowthFactor
} else {
    0.0
}
$iterativeMode = ($maxAttempts -gt 1)
$testFileGuardEnabled = $iterativeMode -and $fixerConfig.ContainsKey('TestFileGuard') -and [bool]$fixerConfig.TestFileGuard
$diffGrowthGuardEnabled = $iterativeMode -and ($diffGrowthFactor -gt 0)
$testGuardRoots = if ($testFileGuardEnabled) { Get-TestGuardRootList -MergedConfig $config } else { @() }
$branchName = "$($config.Loop.BranchPrefix)-$Experiment"
$resolvedTarget = Resolve-ExperimentTargetFile -CandidatePath $FilePath -ProjectRoot $TargetDir -MergedConfig $config `
    -DisplayTargetName $TargetName -ExperimentNumber $Experiment
$targetFile = $resolvedTarget.TargetFile
$fullTargetPath = $resolvedTarget.FullTargetPath
$firstAttemptDiffLines = $null
$lastFailureStage = $null
$lastFailureDetail = $null
$lastFixResult = $null
$lastApplyResult = $null
$lastBuildResult = $null
$lastTestResult = $null

function Write-IterationLogFile {
    param([string]$FinalOutcome)

    $payload = [ordered]@{
        experiment = $Experiment
        totalAttempts = $iterationEntries.Count
        finalOutcome = $FinalOutcome
        attempts = @($iterationEntries)
    }

    $payload | ConvertTo-Json -Depth 10 | Out-File -FilePath $iterationLogPath -Encoding utf8
    return [PSCustomObject]$payload
}

function Get-IterativeResult {
    param(
        [bool]$Success,
        [int]$Attempt,
        [string]$ExitReason,
        [string]$FailureStage,
        [string]$FailureDetail
    )

    $iterationLog = Write-IterationLogFile -FinalOutcome $(if ($Success) { 'success' } else { $ExitReason })

    return [PSCustomObject][ordered]@{
        Success = $Success
        Attempt = $Attempt
        AttemptCount = $iterationEntries.Count
        ExitReason = $ExitReason
        LastFailureStage = $FailureStage
        FailureDetail = $FailureDetail
        IterationLog = $iterationLog
        IterationLogPath = $iterationLogPath
        IterationLogRelativePath = ConvertTo-IterationRelativePath -Path $iterationLogPath -Root $TargetDir
        FixResult = $lastFixResult
        ApplyResult = $lastApplyResult
        BuildResult = $lastBuildResult
        TestResult = $lastTestResult
        BranchName = $branchName
        BaseBranch = $BaseBranch
        TargetFile = $targetFile
        TargetPath = $fullTargetPath
        CommitSha = if ($lastApplyResult) { $lastApplyResult.CommitSha } else { $null }
    }
}

if (-not $resolvedTarget.Success) {
    Write-Warning "  Cannot apply fix -> target directory does not exist: $targetFile"
    & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
        -Phase 'experiment' -Level 'warning' `
        -Message "Invalid experiment target path: $targetFile" `
        -Experiment $Experiment

    return (Get-IterativeResult -Success $false -Attempt 0 -ExitReason 'invalid_target' -FailureStage 'target' `
            -FailureDetail "Target path could not be resolved: $targetFile")
}

$previousErrors = $null
$currentFileContent = $null

for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
    $attemptTimer = [System.Diagnostics.Stopwatch]::StartNew()
    $attemptDir = Join-Path -Path $experimentDir -ChildPath "iterations\attempt-$attempt"
    if (-not (Test-Path $attemptDir)) {
        New-Item -ItemType Directory -Path $attemptDir -Force | Out-Null
    }

    if ($iterativeMode) {
        Write-Status "  Attempt $attempt/$maxAttempts"
    }

    $lastFixResult = & (Join-Path $PSScriptRoot 'Invoke-FixAgent.ps1') `
        -FilePath $targetFile `
        -Explanation $Explanation `
        -RootCauseDocument $RootCauseDocument `
        -Experiment $Experiment `
        -ConfigPath $ConfigPath `
        -TargetName $TargetName `
        -TargetDir $TargetDir `
        -Attempt $attempt `
        -PreviousErrors $previousErrors `
        -CurrentFileContent $currentFileContent

    if (-not $lastFixResult.Success -or -not $lastFixResult.CodeBlock) {
        $attemptTimer.Stop()
        $lastFailureStage = 'fix'
        $lastFailureDetail = 'Fix agent response did not contain a code block.'
        $iterationEntries.Add([PSCustomObject][ordered]@{
                attempt = $attempt
                stage = 'fix'
                outcome = 'failed'
                durationSec = [math]::Round($attemptTimer.Elapsed.TotalSeconds, 2)
                error = $lastFailureDetail
                artifacts = [ordered]@{
                    fixPrompt = ConvertTo-IterationRelativePath -Path $lastFixResult.PromptPath -Root $TargetDir
                    fixResponse = ConvertTo-IterationRelativePath -Path $lastFixResult.ResponsePath -Root $TargetDir
                }
            })

        return (Get-IterativeResult -Success $false -Attempt $attempt -ExitReason 'fix_failed' -FailureStage $lastFailureStage `
                -FailureDetail $lastFailureDetail)
    }

    Write-Status "  Applying fix to: $targetFile"
    $lastApplyResult = & (Join-Path $PSScriptRoot 'Apply-Suggestion.ps1') `
        -FilePath $targetFile `
        -NewContent $lastFixResult.CodeBlock `
        -Description (Limit-String $Explanation 120) `
        -Experiment $Experiment `
        -BaseBranch $BaseBranch `
        -ConfigPath $ConfigPath `
        -TargetDir $TargetDir

    if (-not $lastApplyResult.Success) {
        $attemptTimer.Stop()
        $lastFailureStage = 'apply'
        $lastFailureDetail = if ($lastApplyResult.Description) { $lastApplyResult.Description } else { 'Failed to apply suggested code change.' }
        $iterationEntries.Add([PSCustomObject][ordered]@{
                attempt = $attempt
                stage = 'apply'
                outcome = 'failed'
                durationSec = [math]::Round($attemptTimer.Elapsed.TotalSeconds, 2)
                error = $lastFailureDetail
                artifacts = [ordered]@{
                    fixPrompt = ConvertTo-IterationRelativePath -Path $lastFixResult.AttemptPromptPath -Root $TargetDir
                    fixResponse = ConvertTo-IterationRelativePath -Path $lastFixResult.AttemptResponsePath -Root $TargetDir
                }
            })

        return (Get-IterativeResult -Success $false -Attempt $attempt -ExitReason 'apply_failed' -FailureStage $lastFailureStage `
                -FailureDetail $lastFailureDetail)
    }

    Write-Status "  Build + verify..."
    $attemptArtifacts = [ordered]@{
        fixPrompt = ConvertTo-IterationRelativePath -Path $lastFixResult.AttemptPromptPath -Root $TargetDir
        fixResponse = ConvertTo-IterationRelativePath -Path $lastFixResult.AttemptResponsePath -Root $TargetDir
    }

    $diffLines = Get-LatestCommitDiffLineCount -RepositoryRoot $TargetDir
    if ($null -eq $firstAttemptDiffLines) {
        $firstAttemptDiffLines = [Math]::Max($diffLines, 1)
    }

    if ($testFileGuardEnabled) {
        $changedFiles = Get-LatestCommitChangedFileList -RepositoryRoot $TargetDir
        $changedTestFiles = Get-ChangedTestFileList -ChangedFiles $changedFiles -GuardRoots $testGuardRoots
        if ($changedTestFiles.Count -gt 0) {
            $attemptTimer.Stop()
            $lastFailureStage = 'guard'
            $lastFailureDetail = "Fix modified test files: $($changedTestFiles -join ', ')"
            $currentFileContent = Get-Content -Path $fullTargetPath -Raw
            $previousErrors = Format-RetryContext -Attempt $attempt -Stage 'guard' -ErrorOutput $lastFailureDetail

            $iterationEntries.Add([PSCustomObject][ordered]@{
                    attempt = $attempt
                    stage = 'guard'
                    outcome = 'rejected'
                    durationSec = [math]::Round($attemptTimer.Elapsed.TotalSeconds, 2)
                    error = $lastFailureDetail
                    diffLines = $diffLines
                    changedFiles = @($changedTestFiles)
                    commitSha = $lastApplyResult.CommitSha
                    artifacts = $attemptArtifacts
                })

            if ($attempt -lt $maxAttempts) {
                $null = & (Join-Path $PSScriptRoot 'Revert-ExperimentCode.ps1') `
                    -BranchName $branchName `
                    -FilePath $targetFile `
                    -Experiment $Experiment `
                    -Outcome 'retry' `
                    -Description $Explanation `
                    -ConfigPath $ConfigPath `
                    -SoftReset `
                    -TargetDir $TargetDir
                continue
            }

            return (Get-IterativeResult -Success $false -Attempt $attempt -ExitReason 'retry_budget_exhausted' `
                    -FailureStage $lastFailureStage -FailureDetail $lastFailureDetail)
        }
    }

    if ($diffGrowthGuardEnabled -and $attempt -gt 1 -and $diffLines -gt ($firstAttemptDiffLines * $diffGrowthFactor)) {
        $attemptTimer.Stop()
        $lastFailureStage = 'guard'
        $maxAllowedLines = [math]::Round($firstAttemptDiffLines * $diffGrowthFactor, 2)
        $lastFailureDetail = "Diff grew to $diffLines lines (limit: $maxAllowedLines)."
        $currentFileContent = Get-Content -Path $fullTargetPath -Raw
        $previousErrors = Format-RetryContext -Attempt $attempt -Stage 'guard' -ErrorOutput $lastFailureDetail

        $iterationEntries.Add([PSCustomObject][ordered]@{
                attempt = $attempt
                stage = 'guard'
                outcome = 'rejected'
                durationSec = [math]::Round($attemptTimer.Elapsed.TotalSeconds, 2)
                error = $lastFailureDetail
                diffLines = $diffLines
                commitSha = $lastApplyResult.CommitSha
                artifacts = $attemptArtifacts
            })

        if ($attempt -lt $maxAttempts) {
            $null = & (Join-Path $PSScriptRoot 'Revert-ExperimentCode.ps1') `
                -BranchName $branchName `
                -FilePath $targetFile `
                -Experiment $Experiment `
                -Outcome 'retry' `
                -Description $Explanation `
                -ConfigPath $ConfigPath `
                -SoftReset `
                -TargetDir $TargetDir
            continue
        }

        return (Get-IterativeResult -Success $false -Attempt $attempt -ExitReason 'retry_budget_exhausted' `
                -FailureStage $lastFailureStage -FailureDetail $lastFailureDetail)
    }

    $attemptBuildLogPath = Join-Path -Path $attemptDir -ChildPath 'build.log'
    $lastBuildResult = & (Join-Path $PSScriptRoot 'Invoke-Build.ps1') `
        -ConfigPath $ConfigPath `
        -TargetDir $TargetDir `
        -Experiment $Experiment `
        -Attempt $attempt `
        -AdditionalLogPath $attemptBuildLogPath
    $attemptArtifacts.buildLog = ConvertTo-IterationRelativePath -Path $attemptBuildLogPath -Root $TargetDir

    if (-not $lastBuildResult.Success) {
        $attemptTimer.Stop()
        $lastFailureStage = 'build'
        $lastFailureDetail = Limit-IterationErrorText -Text $lastBuildResult.Output
        $currentFileContent = Get-Content -Path $fullTargetPath -Raw
        $previousErrors = Format-RetryContext -Attempt $attempt -Stage 'build' -ErrorOutput $lastBuildResult.Output

        $iterationEntries.Add([PSCustomObject][ordered]@{
                attempt = $attempt
                stage = 'build'
                outcome = 'failed'
                durationSec = [math]::Round($attemptTimer.Elapsed.TotalSeconds, 2)
                error = $lastFailureDetail
                diffLines = $diffLines
                commitSha = $lastApplyResult.CommitSha
                artifacts = $attemptArtifacts
            })

        if ($attempt -lt $maxAttempts) {
            $null = & (Join-Path $PSScriptRoot 'Revert-ExperimentCode.ps1') `
                -BranchName $branchName `
                -FilePath $targetFile `
                -Experiment $Experiment `
                -Outcome 'retry' `
                -Description $Explanation `
                -ConfigPath $ConfigPath `
                -SoftReset `
                -TargetDir $TargetDir
            continue
        }

        $exitReason = if ($iterativeMode) { 'retry_budget_exhausted' } else { 'build_failure' }
        return (Get-IterativeResult -Success $false -Attempt $attempt -ExitReason $exitReason -FailureStage $lastFailureStage `
                -FailureDetail $lastFailureDetail)
    }

    $attemptTestLogPath = Join-Path -Path $attemptDir -ChildPath 'e2e-tests.log'
    $attemptTrxPath = Join-Path -Path $attemptDir -ChildPath 'e2e-results.trx'
    $lastTestResult = & (Join-Path $PSScriptRoot 'Invoke-E2ETests.ps1') `
        -ConfigPath $ConfigPath `
        -TargetDir $TargetDir `
        -Experiment $Experiment `
        -Attempt $attempt `
        -AdditionalLogPath $attemptTestLogPath `
        -AdditionalTrxPath $attemptTrxPath
    $attemptArtifacts.e2eLog = ConvertTo-IterationRelativePath -Path $attemptTestLogPath -Root $TargetDir
    $attemptArtifacts.e2eResults = ConvertTo-IterationRelativePath -Path $attemptTrxPath -Root $TargetDir

    if (-not $lastTestResult.Success) {
        $attemptTimer.Stop()
        $lastFailureStage = 'test'
        $lastFailureDetail = Limit-IterationErrorText -Text $lastTestResult.Output
        $currentFileContent = Get-Content -Path $fullTargetPath -Raw
        $previousErrors = Format-RetryContext -Attempt $attempt -Stage 'test' -ErrorOutput $lastTestResult.Output

        $iterationEntries.Add([PSCustomObject][ordered]@{
                attempt = $attempt
                stage = 'test'
                outcome = 'failed'
                durationSec = [math]::Round($attemptTimer.Elapsed.TotalSeconds, 2)
                error = $lastFailureDetail
                diffLines = $diffLines
                commitSha = $lastApplyResult.CommitSha
                artifacts = $attemptArtifacts
            })

        if ($attempt -lt $maxAttempts) {
            $null = & (Join-Path $PSScriptRoot 'Revert-ExperimentCode.ps1') `
                -BranchName $branchName `
                -FilePath $targetFile `
                -Experiment $Experiment `
                -Outcome 'retry' `
                -Description $Explanation `
                -ConfigPath $ConfigPath `
                -SoftReset `
                -TargetDir $TargetDir
            continue
        }

        $exitReason = if ($iterativeMode) { 'retry_budget_exhausted' } else { 'test_failure' }
        return (Get-IterativeResult -Success $false -Attempt $attempt -ExitReason $exitReason -FailureStage $lastFailureStage `
                -FailureDetail $lastFailureDetail)
    }

    $attemptTimer.Stop()
    $iterationEntries.Add([PSCustomObject][ordered]@{
            attempt = $attempt
            stage = 'test'
            outcome = 'passed'
            durationSec = [math]::Round($attemptTimer.Elapsed.TotalSeconds, 2)
            diffLines = $diffLines
            commitSha = $lastApplyResult.CommitSha
            artifacts = $attemptArtifacts
        })

    return (Get-IterativeResult -Success $true -Attempt $attempt -ExitReason $null -FailureStage $null -FailureDetail $null)
}

return (Get-IterativeResult -Success $false -Attempt $maxAttempts -ExitReason 'retry_budget_exhausted' `
        -FailureStage $lastFailureStage -FailureDetail $lastFailureDetail)
