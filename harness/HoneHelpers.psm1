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
    - Add-ExperimentMetadatum    — Records experiment entries in run-metadata.json
    - New-ExperimentPR          — Creates GitHub PRs for experiments
    - Build-StackNote           — Builds PR stack context note for stacked-diffs mode
    - Resolve-Hook              — Resolves hook definitions from .hone/config.psd1
    - Invoke-LifecycleHook      — Resolves and dispatches lifecycle hooks
    - Merge-HoneConfig          — Merges engine defaults with target config
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
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSec) {
        try {
            $response = Invoke-RestMethod -Uri $HealthUrl -Method Get -TimeoutSec 2 -ErrorAction Stop
            if ($response.status -eq 'healthy') {
                return $true
            }
        } catch {
            Write-Verbose "Health check at $($stopwatch.Elapsed.TotalSeconds.ToString('F0'))s/${TimeoutSec}s — not ready"
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
    .DESCRIPTION
        Uses System.Diagnostics.ProcessStartInfo.ArgumentList for proper argument
        quoting — essential because the prompt argument contains spaces, newlines,
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
        [int]$ConsecutiveFailures = 0,
        [System.Collections.IDictionary]$AdditionalProperties
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

    if ($AdditionalProperties) {
        foreach ($key in $AdditionalProperties.Keys) {
            $experimentMeta[$key] = $AdditionalProperties[$key]
        }
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

    $fixturePublish = Get-HarnessTestingPublishDefinition -Experiment $Experiment
    if ($fixturePublish -and $fixturePublish.ContainsKey('SkipPRCreation') -and $fixturePublish.SkipPRCreation) {
        $fakeNumber = if ($fixturePublish.ContainsKey('PrNumber') -and $fixturePublish.PrNumber) { $fixturePublish.PrNumber } else { (1000 + $Experiment) }
        $fakeUrl = if ($fixturePublish.ContainsKey('PrUrl') -and $fixturePublish.PrUrl) {
            $fixturePublish.PrUrl
        } else {
            "https://example.invalid/hone/fixture/pull/$fakeNumber"
        }

        & (Join-Path $PSScriptRoot 'Write-HoneLog.ps1') `
            -Phase 'publish' -Level 'info' `
            -Message "Fixture PR created: $fakeUrl" `
            -Experiment $Experiment `
            -Data @{ prUrl = "$fakeUrl"; prNumber = $fakeNumber; baseBranch = $BaseBranch; outcome = $outcomeTag; fixture = $true }

        return [PSCustomObject]@{
            Success = $true
            PrUrl = "$fakeUrl"
            PrNumber = $fakeNumber
        }
    }

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

        Write-Status "  ✓ Pull request created: $result (base: $BaseBranch) [$outcomeTag]"

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

function Get-HarnessTestingPublishDefinition {
    <#
    .SYNOPSIS
        Resolves the publish fixture definition for the current experiment, if any.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [int]$Experiment = 0,
        [string]$TargetDir
    )

    $fixtureTargetDir = if ($TargetDir) { $TargetDir } else { $env:HONE_HARNESS_TEST_TARGET_DIR }
    if (-not $fixtureTargetDir) {
        return $null
    }

    $targetConfigPath = Join-Path -Path $fixtureTargetDir -ChildPath '.hone' -AdditionalChildPath 'config.psd1'
    if (-not (Test-Path -Path $targetConfigPath)) {
        return $null
    }

    $engineConfig = Get-HoneConfig
    $targetCfg = Import-PowerShellDataFile -Path $targetConfigPath
    $mergedConfig = Merge-HoneConfig -Engine $engineConfig -Target $targetCfg
    $fixture = Get-HarnessTestingFixture -Config $mergedConfig -TargetDir $fixtureTargetDir
    if (-not $fixture) {
        return $null
    }

    return (Get-HarnessTestingRuntimeDefinition -Fixture $fixture -Path @('Publish') -Experiment $Experiment)
}

function Invoke-ExperimentBranchPush {
    <#
    .SYNOPSIS
        Pushes an experiment branch to origin, or simulates the push for fixtures.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]$BranchName,

        [int]$Experiment = 0,

        [string]$TargetDir
    )

    $fixturePublish = Get-HarnessTestingPublishDefinition -Experiment $Experiment -TargetDir $TargetDir
    if ($fixturePublish) {
        $pushSucceeded = if ($fixturePublish.ContainsKey('PushSuccess')) { [bool]$fixturePublish.PushSuccess } else { $true }
        $pushExitCode = if ($fixturePublish.ContainsKey('PushExitCode')) { [int]$fixturePublish.PushExitCode } else { $(if ($pushSucceeded) { 0 } else { 1 }) }
        $pushOutput = if ($fixturePublish.ContainsKey('PushOutput') -and $fixturePublish.PushOutput) {
            "$($fixturePublish.PushOutput)"
        } else {
            "Fixture branch push simulated for $BranchName"
        }

        return [PSCustomObject]@{
            Success = $pushSucceeded
            Output = $pushOutput
            ExitCode = $pushExitCode
            UsedFixture = $true
        }
    }

    $pushOutput = @(git push -u origin $BranchName 2>&1)
    $pushExitCode = $LASTEXITCODE

    return [PSCustomObject]@{
        Success = ($pushExitCode -eq 0)
        Output = ($pushOutput | Out-String).Trim()
        ExitCode = $pushExitCode
        UsedFixture = $false
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

    $stackParts = @("``$BaseBranch``")
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
    [OutputType([hashtable])]
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
            Write-Verbose "Resolved hook '$HookName' → Script: $resolvedPath"
            return @{ Type = 'Script'; Path = $resolvedPath }
        }
        'Shared' {
            $resolvedPath = Join-Path $HarnessRoot "hooks\$($hook.Name).ps1"
            if (-not (Test-Path $resolvedPath)) {
                throw "Shared hook not found: $resolvedPath (declared in Hooks.$HookName)"
            }
            Write-Verbose "Resolved hook '$HookName' → Shared: $resolvedPath"
            return @{ Type = 'Script'; Path = $resolvedPath }
        }
        'Command' {
            Write-Verbose "Resolved hook '$HookName' → Command"
            return $hook
        }
        'Http' {
            Write-Verbose "Resolved hook '$HookName' → Http"
            return $hook
        }
        'Skip' {
            Write-Verbose "Resolved hook '$HookName' → Skip"
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

    $fixture = if ($Config) { Get-HarnessTestingFixture -Config $Config -TargetDir $TargetDir } else { $null }
    if ($fixture) {
        $fixtureExperiment = 0
        if ($Experiment -and ($Experiment -as [int])) {
            $fixtureExperiment = [int]$Experiment
        }

        $hookFixture = Get-HarnessTestingRuntimeDefinition -Fixture $fixture -Path @('Hooks', $Name) -Experiment $fixtureExperiment
        if ($hookFixture) {
            $result = [ordered]@{
                Success = if ($hookFixture.ContainsKey('Success')) { [bool]$hookFixture.Success } else { $true }
                Message = if ($hookFixture.ContainsKey('Message')) { $hookFixture.Message } else { "Fixture hook '$Name' completed" }
                Duration = [timespan]::Zero
                Artifacts = @()
            }

            foreach ($key in $hookFixture.Keys) {
                if ($key -in @('Success', 'Message')) {
                    continue
                }

                $result[$key] = $hookFixture[$key]
            }

            if ($Name -eq 'Start' -and $result.Contains('ActualBaseUrl') -and -not $result.Contains('BaseUrl')) {
                $result['BaseUrl'] = $result['ActualBaseUrl']
            }

            return [PSCustomObject]$result
        }
    }

    $resolved = Resolve-Hook -HookName $Name -TargetConfig $TargetConfig -TargetDir $TargetDir -HarnessRoot $HarnessRoot

    $hookScript = Join-Path $HarnessRoot 'hooks\Invoke-Hook.ps1'
    Write-Verbose "Dispatching lifecycle hook '$Name' via $hookScript"
    & $hookScript -Hook $resolved -TargetPath $TargetDir -Config $Config -BaseUrl $BaseUrl -Experiment $Experiment
}

function Assert-LifecycleHookSucceeded {
    <#
    .SYNOPSIS
        Verifies that a lifecycle hook returned a successful result.
    .DESCRIPTION
        Raises a descriptive terminating error when the hook returned no result
        or reported Success = $false. Returns the original result object on
        success so callers can keep piping/assigning it naturally.
    .PARAMETER Name
        Name of the lifecycle hook that was executed.
    .PARAMETER Result
        Result object returned by Invoke-LifecycleHook / Invoke-Hook.ps1.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] $Result
    )

    if ($null -eq $Result) {
        throw "Lifecycle hook '$Name' returned no result."
    }

    if (-not $Result.Success) {
        $message = if ($Result.PSObject.Properties['Message'] -and $Result.Message) {
            $Result.Message
        } else {
            'no error message was returned.'
        }
        throw "Lifecycle hook '$Name' failed: $message"
    }

    return $Result
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
    [OutputType([hashtable])]
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

function Copy-HoneHashtable {
    <#
    .SYNOPSIS
        Deep-copies a hashtable-like dictionary.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Dictionary
    )

    $copy = @{}

    foreach ($key in $Dictionary.Keys) {
        if ($Dictionary[$key] -is [System.Collections.IDictionary]) {
            $copy[$key] = Copy-HoneHashtable -Dictionary $Dictionary[$key]
        } else {
            $copy[$key] = $Dictionary[$key]
        }
    }

    return $copy
}

function Convert-HoneK6SummaryToMetricSet {
    <#
    .SYNOPSIS
        Converts a k6 JSON summary payload into Hone's metric object shape.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        $Summary,

        [Parameter(Mandatory)]
        [int]$Experiment,

        [int]$Run = 1,

        [string]$SummaryPath
    )

    $failedCount = 0
    if ($null -ne $Summary.metrics.http_req_failed.passes) {
        $failedCount = [int]$Summary.metrics.http_req_failed.passes
    }

    $failedRate = 0
    if ($null -ne $Summary.metrics.http_req_failed.value) {
        $failedRate = $Summary.metrics.http_req_failed.value
    }

    return [pscustomobject][ordered]@{
        Timestamp = (Get-Date -Format 'o')
        Experiment = $Experiment
        Run = $Run
        HttpReqDuration = [ordered]@{
            Avg = $Summary.metrics.http_req_duration.avg
            P50 = $Summary.metrics.http_req_duration.med
            P90 = $Summary.metrics.http_req_duration.'p(90)'
            P95 = $Summary.metrics.http_req_duration.'p(95)'
            P99 = $Summary.metrics.http_req_duration.'p(99)'
            Max = $Summary.metrics.http_req_duration.max
        }
        HttpReqs = [ordered]@{
            Count = $Summary.metrics.http_reqs.count
            Rate = $Summary.metrics.http_reqs.rate
        }
        HttpReqFailed = [ordered]@{
            Count = $failedCount
            Rate = $failedRate
        }
        SummaryPath = $SummaryPath
    }
}

function Get-HarnessTestingContract {
    <#
    .SYNOPSIS
        Loads the canonical harness-testing contract data.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param()

    $contractPath = Join-Path -Path $PSScriptRoot -ChildPath 'test-fixtures' -AdditionalChildPath 'contracts\harness-testing-contract.psd1'
    if (-not (Test-Path -Path $contractPath)) {
        throw "Harness-testing contract file not found: $contractPath"
    }

    return (Import-PowerShellDataFile -Path $contractPath)
}

function Get-HarnessTestingFixture {
    <#
    .SYNOPSIS
        Loads the deterministic harness-testing fixture manifest for a target.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Config,

        [string]$TargetDir
    )

    if (-not $TargetDir) {
        return $null
    }

    if (-not $Config.ContainsKey('HarnessTesting') -or -not $Config.HarnessTesting.Enabled) {
        return $null
    }

    $manifestRelPath = if ($Config.HarnessTesting.ContainsKey('ManifestPath') -and $Config.HarnessTesting.ManifestPath) {
        $Config.HarnessTesting.ManifestPath
    } else {
        '.hone\fixtures\fixture.psd1'
    }

    $manifestPath = if ([System.IO.Path]::IsPathRooted($manifestRelPath)) {
        $manifestRelPath
    } else {
        Join-Path -Path $TargetDir -ChildPath $manifestRelPath
    }

    if (-not (Test-Path -Path $manifestPath)) {
        return $null
    }

    $fixture = Import-PowerShellDataFile -Path $manifestPath
    $fixture['_ManifestPath'] = $manifestPath
    $fixture['_TargetDir'] = $TargetDir

    return $fixture
}

function Resolve-HarnessTestingFixtureDefinition {
    <#
    .SYNOPSIS
        Resolves a fixture definition, applying any experiment-specific overrides.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Definition,

        [int]$Experiment = -1,

        [int]$Attempt = -1
    )

    $hasDefault = $Definition.Contains('Default')
    $hasByExperiment = $Definition.Contains('ByExperiment')

    if (-not $hasDefault -and -not $hasByExperiment) {
        return (Copy-HoneHashtable -Dictionary $Definition)
    }

    $resolved = @{}

    if ($hasDefault -and $Definition['Default'] -is [System.Collections.IDictionary]) {
        $resolved = Copy-HoneHashtable -Dictionary $Definition['Default']
    }

    if ($hasByExperiment -and $Experiment -ge 0 -and $Definition['ByExperiment'] -is [System.Collections.IDictionary]) {
        $byExperiment = $Definition['ByExperiment']
        $experimentKey = "$Experiment"

        if ($byExperiment.Contains($experimentKey) -and $byExperiment[$experimentKey] -is [System.Collections.IDictionary]) {
            foreach ($key in $byExperiment[$experimentKey].Keys) {
                if ($byExperiment[$experimentKey][$key] -is [System.Collections.IDictionary]) {
                    $resolved[$key] = Copy-HoneHashtable -Dictionary $byExperiment[$experimentKey][$key]
                } else {
                    $resolved[$key] = $byExperiment[$experimentKey][$key]
                }
            }
        } elseif ($byExperiment.Contains('*') -and $byExperiment['*'] -is [System.Collections.IDictionary]) {
            foreach ($key in $byExperiment['*'].Keys) {
                if ($byExperiment['*'][$key] -is [System.Collections.IDictionary]) {
                    $resolved[$key] = Copy-HoneHashtable -Dictionary $byExperiment['*'][$key]
                } else {
                    $resolved[$key] = $byExperiment['*'][$key]
                }
            }
        }
    }

    if ($Definition.Contains('ByAttempt') -and $Attempt -ge 0 -and $Definition['ByAttempt'] -is [System.Collections.IDictionary]) {
        $byAttempt = $Definition['ByAttempt']
        $attemptKey = "$Attempt"

        if ($byAttempt.Contains($attemptKey) -and $byAttempt[$attemptKey] -is [System.Collections.IDictionary]) {
            foreach ($key in $byAttempt[$attemptKey].Keys) {
                if ($byAttempt[$attemptKey][$key] -is [System.Collections.IDictionary]) {
                    $resolved[$key] = Copy-HoneHashtable -Dictionary $byAttempt[$attemptKey][$key]
                } else {
                    $resolved[$key] = $byAttempt[$attemptKey][$key]
                }
            }
        } elseif ($byAttempt.Contains('*') -and $byAttempt['*'] -is [System.Collections.IDictionary]) {
            foreach ($key in $byAttempt['*'].Keys) {
                if ($byAttempt['*'][$key] -is [System.Collections.IDictionary]) {
                    $resolved[$key] = Copy-HoneHashtable -Dictionary $byAttempt['*'][$key]
                } else {
                    $resolved[$key] = $byAttempt['*'][$key]
                }
            }
        }
    }

    if ($resolved.Count -eq 0) {
        return $null
    }

    return $resolved
}

function Get-HarnessTestingRuntimeDefinition {
    <#
    .SYNOPSIS
        Resolves a runtime definition from a fixture manifest.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Fixture,

        [Parameter(Mandatory)]
        [string[]]$Path,

        [int]$Experiment = -1,

        [int]$Attempt = -1
    )

    if (-not $Fixture.ContainsKey('Runtime')) {
        return $null
    }

    $cursor = $Fixture['Runtime']
    foreach ($segment in $Path) {
        if (-not ($cursor -is [System.Collections.IDictionary]) -or -not $cursor.Contains($segment)) {
            return $null
        }

        $cursor = $cursor[$segment]
    }

    if ($cursor -is [System.Collections.IDictionary]) {
        return (Resolve-HarnessTestingFixtureDefinition -Definition $cursor -Experiment $Experiment -Attempt $Attempt)
    }

    return $null
}

function Resolve-HarnessTestingFixturePath {
    <#
    .SYNOPSIS
        Resolves a fixture-relative asset path to an absolute path.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [hashtable]$Fixture,

        [string]$Path
    )

    if (-not $Path) {
        return $null
    }

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    if ($Path.StartsWith('__HARNESS_ROOT__\', [System.StringComparison]::OrdinalIgnoreCase)) {
        return (Join-Path -Path $PSScriptRoot -ChildPath $Path.Substring('__HARNESS_ROOT__\'.Length))
    }

    $baseDir = $PSScriptRoot
    if ($Fixture -and $Fixture.ContainsKey('_ManifestPath') -and $Fixture['_ManifestPath']) {
        $baseDir = Split-Path -Path $Fixture['_ManifestPath'] -Parent
    } elseif ($Fixture -and $Fixture.ContainsKey('_TargetDir') -and $Fixture['_TargetDir']) {
        $baseDir = $Fixture['_TargetDir']
    }

    return (Join-Path -Path $baseDir -ChildPath $Path)
}

function Get-HarnessTestingMockResponsePath {
    <#
    .SYNOPSIS
        Resolves the mock response path for a deterministic fixture agent.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Config,

        [string]$TargetDir,

        [Parameter(Mandatory)]
        [ValidateSet('Analysis', 'Classification', 'Fix')]
        [string]$Agent,

        [int]$Experiment = 0,

        [int]$Attempt = -1
    )

    $fixture = Get-HarnessTestingFixture -Config $Config -TargetDir $TargetDir
    if (-not $fixture) {
        return $null
    }

    $definition = Get-HarnessTestingRuntimeDefinition -Fixture $fixture -Path @('Agents', $Agent) -Experiment $Experiment -Attempt $Attempt
    if (-not $definition -or -not $definition.ContainsKey('MockResponsePath') -or -not $definition.MockResponsePath) {
        return $null
    }

    return (Resolve-HarnessTestingFixturePath -Fixture $fixture -Path $definition.MockResponsePath)
}

Export-ModuleMember -Function Write-Status, Get-HoneConfig, Wait-ApiHealthy, Limit-String, Invoke-CopilotWithTimeout, Undo-ExperimentBranch, Add-ExperimentMetadatum, New-ExperimentPR, Invoke-ExperimentBranchPush, Build-StackNote, Resolve-Hook, Invoke-LifecycleHook, Assert-LifecycleHookSucceeded, Merge-HoneConfig, Convert-HoneK6SummaryToMetricSet, Get-HarnessTestingContract, Get-HarnessTestingFixture, Get-HarnessTestingRuntimeDefinition, Resolve-HarnessTestingFixturePath, Get-HarnessTestingMockResponsePath
