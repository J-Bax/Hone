∩╗┐<#
.SYNOPSIS
    Shared helper module for the Hone harness.

.DESCRIPTION
    Contains common functions used across multiple harness scripts:
    - Write-Status              ΓÇö Timestamped status output with box-drawing support
    - Get-HoneConfig            ΓÇö Centralized config loading from .psd1
    - Wait-ApiHealthy           ΓÇö HTTP health check polling with configurable retry
    - Limit-String              ΓÇö Word-boundary-aware string truncation
    - Invoke-CopilotWithTimeout ΓÇö Runs copilot CLI with a timeout guard
    - Undo-ExperimentBranch     ΓÇö Legacy-mode branch rollback
    - Add-ExperimentMetadatum    ΓÇö Records experiment entries in run-metadata.json
    - New-ExperimentPR          ΓÇö Creates GitHub PRs for experiments
    - Build-StackNote           ΓÇö Builds PR stack context note for stacked-diffs mode
    - Resolve-Hook              ΓÇö Resolves hook definitions from .hone/config.psd1
    - Invoke-LifecycleHook      ΓÇö Resolves and dispatches lifecycle hooks
    - Merge-HoneConfig          ΓÇö Merges engine defaults with target config
#>

function Write-Status {
    <#
    .SYNOPSIS
        Writes a timestamped status message to the information stream.
        Box-drawing lines and blank lines are passed through without timestamps.
    #>
    param([string]$Message)
    if ($Message -match '^\s*$' -or $Message -match '^[ΓöüΓòÉΓöÇΓòöΓòÜΓòùΓò¥ΓòæΓòáΓòúΓòªΓò⌐]') {
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
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSec) {
        try {
            $response = Invoke-RestMethod -Uri $HealthUrl -Method Get -TimeoutSec 2 -ErrorAction Stop
            if ($response.status -eq 'healthy') {
                return $true
            }
        } catch {
            Write-Verbose "Health check at $($stopwatch.Elapsed.TotalSeconds.ToString('F0'))s/${TimeoutSec}s ΓÇö not ready"
        }
        $remainingSec = $TimeoutSec - $stopwatch.Elapsed.TotalSeconds
        if ($remainingSec -le 0) { break }
        Start-Sleep -Seconds ([Math]::Min($IntervalSec, [Math]::Max(1, [int]$remainingSec)))
    }
    return $false
}

function Limit-String {
    <#
    .SYNOPSIS
        Truncates a string at the last word boundary before MaxLength,
        appending "ΓÇª" when truncated.
    #>
    param(
        [string]$Text,
        [int]$MaxLength = 120
    )
    if (-not $Text -or $Text.Length -le $MaxLength) { return $Text }
    $truncated = $Text.Substring(0, $MaxLength)
    $lastSpace = $truncated.LastIndexOf(' ')
    if ($lastSpace -gt ($MaxLength * 0.5)) {
        return $truncated.Substring(0, $lastSpace) + 'ΓÇª'
    }
    return $truncated + 'ΓÇª'
}

function Invoke-CopilotWithTimeout {
    <#
    .SYNOPSIS
        Runs the copilot CLI with a timeout. Kills the process if it exceeds the deadline.
    .DESCRIPTION
        Uses System.Diagnostics.ProcessStartInfo.ArgumentList for proper argument
        quoting ΓÇö essential because the prompt argument contains spaces, newlines,
        and special characters that Start-Process -ArgumentList would mangle.
    .PARAMETER ArgumentList
        Arguments to pass to copilot as individual elements. Each element is
        properly quoted by the .NET runtime (e.g., @('--agent', 'hone-analyst',
        '-p', $prompt)).
    .PARAMETER TimeoutSec
        Maximum seconds to wait. Default: 600.
    .PARAMETER WorkingDirectory
        Working directory for the copilot process. When set, the agent's file
        tools resolve relative paths from this directory. Used to point agents
        at the target project rather than the harness repo.
    .OUTPUTS
        PSCustomObject with Success, Output, TimedOut, ExitCode properties.
    #>
    param(
        [Parameter(Mandatory)]
        [string[]]$ArgumentList,
        [int]$TimeoutSec = 600,
        [string]$WorkingDirectory
    )
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = 'copilot'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    if ($WorkingDirectory) {
        $psi.WorkingDirectory = $WorkingDirectory
    }
    foreach ($arg in $ArgumentList) {
        $psi.ArgumentList.Add($arg)
    }

    $proc = [System.Diagnostics.Process]::new()
    $proc.StartInfo = $psi
    $proc.Start() | Out-Null

    # Read streams asynchronously to prevent deadlocks from buffer fill
    $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
    $null = $proc.StandardError.ReadToEndAsync()

    $exited = $proc.WaitForExit($TimeoutSec * 1000)
    if (-not $exited) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { Write-Verbose "Failed to stop process $($proc.Id): $_" }
        $partialOutput = ''
        try { $partialOutput = $stdoutTask.GetAwaiter().GetResult() } catch { Write-Verbose "Failed to read stdout: $_" }
        return [PSCustomObject]@{
            Success = $false
            Output = $partialOutput
            TimedOut = $true
            ExitCode = -1
        }
    }

    $output = $stdoutTask.GetAwaiter().GetResult()
    return [PSCustomObject]@{
        Success = ($proc.ExitCode -eq 0)
        Output = $output
        TimedOut = $false
        ExitCode = $proc.ExitCode
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
        [string]$RestoreBranch = 'master',
        [Parameter(Mandatory)][string]$TargetDir
    )
    try {
        Push-Location $TargetDir
        git checkout $RestoreBranch 2>&1 | Out-Null
        git branch -D $BranchName 2>&1 | Out-Null
        Pop-Location
    } catch {
        Pop-Location -ErrorAction SilentlyContinue
        Write-Warning "Rollback failed for branch '$BranchName': $_"
    }
}

function Add-ExperimentMetadatum {
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
        Experiment = $Experiment
        StartedAt = $StartedAt
        CompletedAt = (Get-Date -Format 'o')
        Improved = $Improved
        Regression = $Regression
        P95 = $p95
        RPS = $rps
        Outcome = $Outcome
        BranchName = $BranchName
        BaseBranch = $BaseBranch
        PrNumber = if ($PrNumber) { $PrNumber } else { $null }
        PrUrl = if ($PrUrl) { "$PrUrl" } else { $null }
        StaleCount = $StaleCount
        ConsecutiveFailures = $ConsecutiveFailures
    }

    if ($RunMetadata.Experiments -is [System.Collections.IList]) {
        $RunMetadata.Experiments += [PSCustomObject]$experimentMeta
    } else {
        $RunMetadata | Add-Member -NotePropertyName Experiments -NotePropertyValue @([PSCustomObject]$experimentMeta) -Force
    }

    $RunMetadata | ConvertTo-Json -Depth 10 | Out-File -FilePath $MetadataPath -Encoding utf8
}

function New-ExperimentPR {
    <#
    .SYNOPSIS
        Creates a GitHub pull request for an experiment (accepted or rejected).
    #>
    [CmdletBinding(SupportsShouldProcess)]
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

    if ($PSCmdlet.ShouldProcess('GitHub PR', 'Create')) {
        $result = gh pr create `
            --base $BaseBranch `
            --head $BranchName `
            --title $prTitle `
            --body $Body 2>&1
    } else {
        return [PSCustomObject]@{
            Success = $true
            PrUrl = $null
            PrNumber = $null
        }
    }

    if ($LASTEXITCODE -eq 0) {
        $extractedNumber = ($result -split '/')[-1]

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'publish' -Level 'info' `
            -Message "Pull request created: $result" `
            -Experiment $Experiment `
            -Data @{ prUrl = "$result"; prNumber = $extractedNumber; baseBranch = $BaseBranch; outcome = $outcomeTag }

        Write-Status "  Γ£ô Pull request created: $result (base: $BaseBranch) [$outcomeTag]"

        return [PSCustomObject]@{
            Success = $true
            PrUrl = "$result"
            PrNumber = $extractedNumber
        }
    } else {
        Write-Warning "  Failed to create pull request: $result"

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'publish' -Level 'error' `
            -Message "Failed to create pull request for experiment $Experiment ($Outcome): $result" `
            -Experiment $Experiment `
            -Data @{ baseBranch = $BaseBranch; outcome = $Outcome; error = "$result" }

        return [PSCustomObject]@{
            Success = $false
            PrUrl = $null
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
        $tag = if ($pr.Outcome -eq 'improved') { 'Γ£ô' } else { 'Γ£ù' }
        $stackParts += "PR #$($pr.Number) (experiment-$($pr.Experiment)) $tag"
    }
    $stackParts += "**this PR** (experiment-$Experiment) $OutcomeTag"
    $stackLine = $stackParts -join ' ΓåÆ '

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

function Resolve-Hook {
    <#
    .SYNOPSIS
        Resolves a hook definition from .hone/config.psd1 into an executable descriptor.
    .DESCRIPTION
        Looks up the named hook in TargetConfig.Hooks and resolves its path:
        - Script : resolves relative path under TargetDir
        - Shared : resolves to a built-in hook under HarnessRoot/hooks/
        - Command, Http, Skip : returned as-is
    .PARAMETER HookName
        Name of the hook to resolve (e.g., 'Build', 'Start', 'Verify').
    .PARAMETER TargetConfig
        The target project's .hone/config.psd1 hashtable.
    .PARAMETER TargetDir
        Root directory of the target project.
    .PARAMETER HarnessRoot
        Root directory of the Hone harness.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$HookName,
        [Parameter(Mandatory)] [hashtable]$TargetConfig,
        [Parameter(Mandatory)] [string]$TargetDir,
        [Parameter(Mandatory)] [string]$HarnessRoot
    )

    $hook = $TargetConfig.Hooks[$HookName]
    if (-not $hook) {
        throw ".hone/config.psd1 must declare Hooks.$HookName (use Type = 'Skip' if not needed)"
    }

    switch ($hook.Type) {
        'Script' {
            $resolvedPath = Join-Path $TargetDir $hook.Path
            if (-not (Test-Path $resolvedPath)) {
                throw "Hook script not found: $resolvedPath (declared in Hooks.$HookName)"
            }
            Write-Verbose "Resolved hook '$HookName' ΓåÆ Script: $resolvedPath"
            return @{ Type = 'Script'; Path = $resolvedPath }
        }
        'Shared' {
            $resolvedPath = Join-Path $HarnessRoot "hooks\$($hook.Name).ps1"
            if (-not (Test-Path $resolvedPath)) {
                throw "Shared hook not found: $resolvedPath (declared in Hooks.$HookName)"
            }
            Write-Verbose "Resolved hook '$HookName' ΓåÆ Shared: $resolvedPath"
            return @{ Type = 'Script'; Path = $resolvedPath }
        }
        'Command' {
            Write-Verbose "Resolved hook '$HookName' ΓåÆ Command"
            return $hook
        }
        'Http' {
            Write-Verbose "Resolved hook '$HookName' ΓåÆ Http"
            return $hook
        }
        'Skip' {
            Write-Verbose "Resolved hook '$HookName' ΓåÆ Skip"
            return $hook
        }
        default {
            throw "Unknown hook type '$($hook.Type)' for Hooks.$HookName"
        }
    }
}

function Invoke-LifecycleHook {
    <#
    .SYNOPSIS
        Resolves and dispatches a lifecycle hook by name.
    .DESCRIPTION
        High-level entry point that resolves a hook definition via Resolve-Hook
        and then dispatches it through the Invoke-Hook.ps1 script.
    .PARAMETER Name
        Name of the lifecycle hook (e.g., 'Build', 'Start', 'Verify').
    .PARAMETER TargetConfig
        The target project's .hone/config.psd1 hashtable.
    .PARAMETER TargetDir
        Root directory of the target project.
    .PARAMETER HarnessRoot
        Root directory of the Hone harness.
    .PARAMETER Config
        Merged harness configuration hashtable passed to the hook.
    .PARAMETER BaseUrl
        Base URL of the running API.
    .PARAMETER Experiment
        Current experiment identifier.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [hashtable]$TargetConfig,
        [Parameter(Mandatory)] [string]$TargetDir,
        [Parameter(Mandatory)] [string]$HarnessRoot,
        [hashtable]$Config,
        [string]$BaseUrl,
        [string]$Experiment
    )

    $resolved = Resolve-Hook -HookName $Name -TargetConfig $TargetConfig -TargetDir $TargetDir -HarnessRoot $HarnessRoot

    $hookScript = Join-Path $HarnessRoot 'hooks\Invoke-Hook.ps1'
    Write-Verbose "Dispatching lifecycle hook '$Name' via $hookScript"
    & $hookScript -Hook $resolved -TargetPath $TargetDir -Config $Config -BaseUrl $BaseUrl -Experiment $Experiment
}

function Merge-HoneConfig {
    <#
    .SYNOPSIS
        Shallow-merges target config on top of engine defaults.
    .DESCRIPTION
        For top-level scalar keys, target values override engine values.
        For top-level hashtable keys (Api, Tolerances, Loop, etc.), merges
        at the section level so partial target overrides are supported.
    .PARAMETER Engine
        Engine defaults hashtable.
    .PARAMETER Target
        Target-specific overrides hashtable.
    .OUTPUTS
        [hashtable] Merged configuration.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [hashtable]$Engine,
        [Parameter(Mandatory)] [hashtable]$Target
    )

    $merged = @{}

    # Start with all engine keys
    foreach ($key in $Engine.Keys) {
        if ($Engine[$key] -is [hashtable]) {
            $merged[$key] = @{}
            foreach ($subKey in $Engine[$key].Keys) {
                $merged[$key][$subKey] = $Engine[$key][$subKey]
            }
        } else {
            $merged[$key] = $Engine[$key]
        }
    }

    # Overlay target keys (target wins)
    foreach ($key in $Target.Keys) {
        if ($Target[$key] -is [hashtable]) {
            if (-not $merged.ContainsKey($key)) {
                $merged[$key] = @{}
            }
            foreach ($subKey in $Target[$key].Keys) {
                $merged[$key][$subKey] = $Target[$key][$subKey]
            }
        } else {
            $merged[$key] = $Target[$key]
        }
    }

    Write-Verbose "Merged config: $($merged.Keys.Count) top-level keys"
    return $merged
}

Export-ModuleMember -Function Write-Status, Get-HoneConfig, Wait-ApiHealthy, Limit-String, Invoke-CopilotWithTimeout, Undo-ExperimentBranch, Add-ExperimentMetadatum, New-ExperimentPR, Build-StackNote, Resolve-Hook, Invoke-LifecycleHook, Merge-HoneConfig
