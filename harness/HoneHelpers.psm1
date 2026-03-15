<#
.SYNOPSIS
    Shared helper module for the Hone harness.

.DESCRIPTION
    Contains common functions used across multiple harness scripts:
    - Write-Status              — Timestamped status output with box-drawing support
    - Get-HoneConfig            — Centralized config loading from .psd1
    - Wait-ApiHealthy           — HTTP health check polling with configurable retry
    - Limit-String              — Word-boundary-aware string truncation
    - Invoke-CopilotWithTimeout — Runs copilot CLI with a timeout guard
    - Undo-ExperimentBranch     — Legacy-mode branch rollback
    - Add-ExperimentMetadata    — Records experiment entries in run-metadata.json
    - New-ExperimentPR          — Creates GitHub PRs for experiments
    - Build-StackNote           — Builds PR stack context note for stacked-diffs mode
#>

function Write-Status {
    <#
    .SYNOPSIS
        Writes a timestamped status message to the information stream.
        Box-drawing lines and blank lines are passed through without timestamps.
    #>
    param([string]$Message)
    if ($Message -match '^\s*$' -or $Message -match '^[━═─╔╚╗╝║╠╣╦╩]') {
        Write-Information $Message -InformationAction Continue
    } else {
        Write-Information "[$(Get-Date -Format 'HH:mm:ss')] $Message" -InformationAction Continue
    }
}

function Get-HoneConfig {
    <#
    .SYNOPSIS
        Loads and returns the Hone configuration hashtable from a .psd1 file.
    .PARAMETER ConfigPath
        Path to the config file. Defaults to config.psd1 in the harness directory.
    .PARAMETER HarnessRoot
        Path to the harness directory. Used to resolve the default ConfigPath.
        Defaults to the directory containing this module.
    #>
    param(
        [string]$ConfigPath,
        [string]$HarnessRoot
    )
    if (-not $HarnessRoot) {
        $HarnessRoot = $PSScriptRoot
    }
    if (-not $ConfigPath) {
        $ConfigPath = Join-Path $HarnessRoot 'config.psd1'
    }
    Import-PowerShellDataFile -Path $ConfigPath
}

function Wait-ApiHealthy {
    <#
    .SYNOPSIS
        Polls an API health endpoint until it reports healthy or the timeout expires.
    .PARAMETER HealthUrl
        Full URL of the health endpoint (e.g., http://localhost:5050/health).
    .PARAMETER TimeoutSec
        Maximum seconds to wait. Default: 90.
    .PARAMETER IntervalSec
        Seconds between retry attempts. Default: 1.
    .OUTPUTS
        [bool] $true if healthy, $false if timeout expired.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$HealthUrl,
        [int]$TimeoutSec = 90,
        [int]$IntervalSec = 1
    )
    $elapsed = 0
    while ($elapsed -lt $TimeoutSec) {
        Start-Sleep -Seconds $IntervalSec
        $elapsed += $IntervalSec
        try {
            $response = Invoke-RestMethod -Uri $HealthUrl -Method Get -TimeoutSec 2 -ErrorAction Stop
            if ($response.status -eq 'healthy') {
                return $true
            }
        }
        catch {
            Write-Verbose "Health check attempt $elapsed/${TimeoutSec}s — not ready"
        }
    }
    return $false
}

function Limit-String {
    <#
    .SYNOPSIS
        Truncates a string at the last word boundary before MaxLength,
        appending "…" when truncated.
    #>
    param(
        [string]$Text,
        [int]$MaxLength = 120
    )
    if (-not $Text -or $Text.Length -le $MaxLength) { return $Text }
    $truncated = $Text.Substring(0, $MaxLength)
    $lastSpace = $truncated.LastIndexOf(' ')
    if ($lastSpace -gt ($MaxLength * 0.5)) {
        return $truncated.Substring(0, $lastSpace) + '…'
    }
    return $truncated + '…'
}

function Invoke-CopilotWithTimeout {
    <#
    .SYNOPSIS
        Runs the copilot CLI with a timeout. Kills the process if it exceeds the deadline.
    .PARAMETER ArgumentList
        Arguments to pass to copilot (e.g., '--agent hone-analyst --model claude-opus-4.6 -p "prompt" -s --no-auto-update --no-ask-user').
    .PARAMETER TimeoutSec
        Maximum seconds to wait. Default: 600.
    .OUTPUTS
        PSCustomObject with Success, Output, TimedOut properties.
    #>
    param(
        [Parameter(Mandatory)]
        [string[]]$ArgumentList,
        [int]$TimeoutSec = 600
    )
    $outFile = [System.IO.Path]::GetTempFileName()
    $errFile = [System.IO.Path]::GetTempFileName()
    try {
        $proc = Start-Process -FilePath 'copilot' -ArgumentList $ArgumentList `
            -PassThru -NoNewWindow -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        $exited = $proc.WaitForExit($TimeoutSec * 1000)
        if (-not $exited) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            $partialOutput = if (Test-Path $outFile) { Get-Content $outFile -Raw } else { '' }
            return [PSCustomObject]@{
                Success  = $false
                Output   = $partialOutput
                TimedOut = $true
                ExitCode = -1
            }
        }
        $output = if (Test-Path $outFile) { Get-Content $outFile -Raw } else { '' }
        return [PSCustomObject]@{
            Success  = ($proc.ExitCode -eq 0)
            Output   = $output
            TimedOut = $false
            ExitCode = $proc.ExitCode
        }
    }
    finally {
        Remove-Item $outFile, $errFile -Force -ErrorAction SilentlyContinue
    }
}

function Undo-ExperimentBranch {
    <#
    .SYNOPSIS
        Rolls back to a target branch and deletes the experiment branch.
        Used in legacy (non-stacked) mode only.
    #>
    param(
        [Parameter(Mandatory)][string]$BranchName,
        [Parameter(Mandatory)][string]$RepoRoot,
        [string]$RestoreBranch = 'master'
    )
    try {
        Push-Location (Join-Path $RepoRoot 'sample-api')
        git checkout $RestoreBranch 2>&1 | Out-Null
        git branch -D $BranchName 2>&1 | Out-Null
        Pop-Location
    }
    catch {
        Pop-Location -ErrorAction SilentlyContinue
        Write-Warning "Rollback failed for branch '$BranchName': $_"
    }
}

function Add-ExperimentMetadata {
    <#
    .SYNOPSIS
        Records an experiment entry in run-metadata.json.
        Called from every code path (including early exits) to ensure no gaps.
    #>
    param(
        [Parameter(Mandatory)][PSCustomObject]$RunMetadata,
        [Parameter(Mandatory)][string]$MetadataPath,
        [int]$Experiment,
        [string]$StartedAt,
        [string]$Outcome,
        [string]$BranchName,
        [string]$BaseBranch = 'master',
        [PSCustomObject]$Metrics,
        [bool]$Improved = $false,
        [bool]$Regression = $false,
        [int]$PrNumber,
        [string]$PrUrl,
        [int]$StaleCount = 0,
        [int]$ConsecutiveFailures = 0
    )

    $p95 = if ($Metrics -and $Metrics.HttpReqDuration) { $Metrics.HttpReqDuration.P95 } else { $null }
    $rps = if ($Metrics -and $Metrics.HttpReqs) { [math]::Round($Metrics.HttpReqs.Rate, 1) } else { $null }

    $experimentMeta = [ordered]@{
        Experiment          = $Experiment
        StartedAt           = $StartedAt
        CompletedAt         = (Get-Date -Format 'o')
        Improved            = $Improved
        Regression          = $Regression
        P95                 = $p95
        RPS                 = $rps
        Outcome             = $Outcome
        BranchName          = $BranchName
        BaseBranch          = $BaseBranch
        PrNumber            = if ($PrNumber) { $PrNumber } else { $null }
        PrUrl               = if ($PrUrl) { "$PrUrl" } else { $null }
        StaleCount          = $StaleCount
        ConsecutiveFailures = $ConsecutiveFailures
    }

    if ($RunMetadata.Experiments -is [System.Collections.IList]) {
        $RunMetadata.Experiments += [PSCustomObject]$experimentMeta
    }
    else {
        $RunMetadata | Add-Member -NotePropertyName Experiments -NotePropertyValue @([PSCustomObject]$experimentMeta) -Force
    }

    $RunMetadata | ConvertTo-Json -Depth 10 | Out-File -FilePath $MetadataPath -Encoding utf8
}

function New-ExperimentPR {
    <#
    .SYNOPSIS
        Creates a GitHub pull request for an experiment (accepted or rejected).
    #>
    param(
        [int]$Experiment,
        [string]$BranchName,
        [string]$BaseBranch,
        [string]$Outcome,
        [string]$Description,
        [string]$Body,
        [switch]$IsDryRun
    )

    $outcomeTag = if ($Outcome -eq 'improved') { 'ACCEPTED' } else { 'REJECTED' }
    $dryRunPrefix = if ($IsDryRun) { '[DRY RUN] ' } else { '' }
    $prTitle = "${dryRunPrefix}hone(experiment-${Experiment})[${outcomeTag}]: $(Limit-String $Description 120)"

    $result = gh pr create `
        --base $BaseBranch `
        --head $BranchName `
        --title $prTitle `
        --body $Body 2>&1

    if ($LASTEXITCODE -eq 0) {
        $extractedNumber = ($result -split '/')[-1]

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'publish' -Level 'info' `
            -Message "Pull request created: $result" `
            -Experiment $Experiment `
            -Data @{ prUrl = "$result"; prNumber = $extractedNumber; baseBranch = $BaseBranch; outcome = $outcomeTag }

        Write-Status "  ✓ Pull request created: $result (base: $BaseBranch) [$outcomeTag]"

        return [PSCustomObject]@{
            Success  = $true
            PrUrl    = "$result"
            PrNumber = $extractedNumber
        }
    }
    else {
        Write-Warning "  Failed to create pull request: $result"

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'publish' -Level 'error' `
            -Message "Failed to create pull request for experiment $Experiment ($Outcome): $result" `
            -Experiment $Experiment `
            -Data @{ baseBranch = $BaseBranch; outcome = $Outcome; error = "$result" }

        return [PSCustomObject]@{
            Success  = $false
            PrUrl    = $null
            PrNumber = $null
        }
    }
}

function Build-StackNote {
    <#
    .SYNOPSIS
        Builds the PR stack context note for stacked-diffs mode.
    #>
    param(
        [array]$PrChain,
        [array]$FailedExperiments,
        [int]$Experiment,
        [string]$OutcomeTag,
        [string]$BaseBranch
    )

    if (-not $PrChain -or $PrChain.Count -eq 0) { return '' }

    $stackParts = @('`master`')
    foreach ($pr in $PrChain) {
        $tag = if ($pr.Outcome -eq 'improved') { '✓' } else { '✗' }
        $stackParts += "PR #$($pr.Number) (experiment-$($pr.Experiment)) $tag"
    }
    $stackParts += "**this PR** (experiment-$Experiment) $OutcomeTag"
    $stackLine = $stackParts -join ' → '

    $prExperimentNums = @($PrChain | ForEach-Object { $_.Experiment })
    $failedBetween = @($FailedExperiments | Where-Object {
        $_.Experiment -gt ($PrChain[-1].Experiment) -and $_.Experiment -lt $Experiment -and
        $_.Experiment -notin $prExperimentNums
    })
    $failedNote = ''
    if ($failedBetween.Count -gt 0) {
        $failedList = ($failedBetween | ForEach-Object { "$($_.Experiment) ($($_.Reason))" }) -join ', '
        $failedNote = "`n`n> **Note:** Experiments $failedList were attempted but did not produce branches."
    }

    return @"

**Stack:** $stackLine

**Base:** ``$BaseBranch`` (review only the incremental change)$failedNote

"@
}

Export-ModuleMember -Function Write-Status, Get-HoneConfig, Wait-ApiHealthy, Limit-String, Invoke-CopilotWithTimeout, Undo-ExperimentBranch, Add-ExperimentMetadata, New-ExperimentPR, Build-StackNote
