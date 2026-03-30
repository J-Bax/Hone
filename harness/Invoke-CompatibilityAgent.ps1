<#
.SYNOPSIS
    Assesses a target project's compatibility with the Hone optimization harness.

.DESCRIPTION
    Performs a multi-phase compatibility assessment of a candidate project:
    1. Pre-probes the target for git info, file structure, and project files
    2. Builds a structured prompt with the pre-probe context
    3. Invokes the hone-compatibility agent to run active probes (build, test, etc.)
    4. Parses the structured JSON report
    5. Displays a human-readable summary
    6. Writes the full report to <target>/.hone-assessment.json

    The agent has tool access (bash/powershell, read, glob, grep) and will
    run real commands against the target to determine compatibility.

.PARAMETER TargetPath
    Root directory of the target project to assess.

.PARAMETER ConfigPath
    Path to the Hone engine config.psd1. Defaults to harness/config.psd1.

.PARAMETER OutputPath
    Path to write the JSON assessment report. Defaults to
    <TargetPath>/.hone-assessment.json.

.PARAMETER Model
    AI model override. Defaults to the engine's configured analysis model.

.EXAMPLE
    .\harness\Invoke-CompatibilityAgent.ps1 -TargetPath C:\Projects\eShopOnWeb

.EXAMPLE
    .\harness\Invoke-CompatibilityAgent.ps1 -TargetPath .\my-api -OutputPath .\assessment.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$TargetPath,

    [string]$ConfigPath,

    [string]$OutputPath,

    [string]$Model
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

# ── Resolve paths ───────────────────────────────────────────────────────────
$targetDir = [System.IO.Path]::GetFullPath($TargetPath)
if (-not (Test-Path $targetDir -PathType Container)) {
    throw "Target directory not found: $targetDir"
}

$harnessRoot = $PSScriptRoot
if (-not $OutputPath) {
    $OutputPath = Join-Path $targetDir '.hone-assessment.json'
}

# ── Pre-probe: gather lightweight context ───────────────────────────────────
Write-Information "Assessing target: $targetDir" -InformationAction Continue

$preProbe = [ordered]@{
    targetPath = $targetDir
}

# Git information
$preProbe.git = [ordered]@{
    isGitRepo = $false
    remoteUrl = $null
    defaultBranch = $null
}

$originalLocation = Get-Location
try {
    Set-Location $targetDir
    $gitDir = & git rev-parse --git-dir 2>$null
    if ($LASTEXITCODE -eq 0 -and $gitDir) {
        $preProbe.git.isGitRepo = $true
        $remoteOutput = & git remote -v 2>$null
        if ($remoteOutput) {
            $preProbe.git.remoteUrl = ($remoteOutput | Select-Object -First 1) -replace '\s+\(fetch\)$', '' -replace '^origin\s+', ''
        }
        $headRef = & git symbolic-ref refs/remotes/origin/HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $headRef) {
            $preProbe.git.defaultBranch = $headRef -replace '^refs/remotes/origin/', ''
        }
    }
} finally {
    Set-Location $originalLocation
}

# Project file detection
$projectFiles = [ordered]@{}
$patterns = @(
    @{ Name = 'dotnet-sln'; Pattern = '*.sln' }
    @{ Name = 'dotnet-csproj'; Pattern = '*.csproj' }
    @{ Name = 'dotnet-global'; Pattern = 'global.json' }
    @{ Name = 'node-package'; Pattern = 'package.json' }
    @{ Name = 'node-tsconfig'; Pattern = 'tsconfig.json' }
    @{ Name = 'go-mod'; Pattern = 'go.mod' }
    @{ Name = 'python-req'; Pattern = 'requirements.txt' }
    @{ Name = 'python-pyproj'; Pattern = 'pyproject.toml' }
    @{ Name = 'rust-cargo'; Pattern = 'Cargo.toml' }
    @{ Name = 'java-maven'; Pattern = 'pom.xml' }
    @{ Name = 'java-gradle'; Pattern = 'build.gradle' }
    @{ Name = 'docker-compose'; Pattern = 'docker-compose.yml' }
    @{ Name = 'dockerfile'; Pattern = 'Dockerfile' }
    @{ Name = 'k6-scenario'; Pattern = '*.js' }
)
foreach ($p in $patterns) {
    $found = Get-ChildItem -Path $targetDir -Filter $p.Pattern -Recurse -Depth 3 -ErrorAction SilentlyContinue |
        Select-Object -First 10 |
        ForEach-Object { $_.FullName.Substring($targetDir.Length).TrimStart('\', '/') }
    if ($found) {
        $projectFiles[$p.Name] = @($found)
    }
}
$preProbe.projectFiles = $projectFiles

# Top-level directory listing
$preProbe.topLevelDirs = @(
    Get-ChildItem -Path $targetDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notmatch '^\.' -and $_.Name -ne 'node_modules' -and $_.Name -ne 'bin' -and $_.Name -ne 'obj' -and $_.Name -ne 'packages' } |
        Select-Object -First 30 |
        ForEach-Object { $_.Name }
)

$preProbe.topLevelFiles = @(
    Get-ChildItem -Path $targetDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notmatch '^\.' -or $_.Name -eq '.gitignore' } |
        Select-Object -First 30 |
        ForEach-Object { $_.Name }
)

# Existing .hone/ directory
$honeDir = Join-Path $targetDir '.hone'
$preProbe.existingHoneDir = (Test-Path $honeDir -PathType Container)
if ($preProbe.existingHoneDir) {
    $preProbe.honeDirContents = @(
        Get-ChildItem -Path $honeDir -Recurse -ErrorAction SilentlyContinue |
            ForEach-Object { $_.FullName.Substring($honeDir.Length).TrimStart('\', '/') }
    )
}

# ── Build prompt ────────────────────────────────────────────────────────────
$preProbeJson = $preProbe | ConvertTo-Json -Depth 5 -Compress

$prompt = @"
Assess the following target project for Hone compatibility.

## Target Pre-Probe Data

``````json
$preProbeJson
``````

## Instructions

1. Use the pre-probe data above as your starting point.
2. The target project is located at: $targetDir
3. Actively investigate by reading key files and running commands.
4. For build and test commands, run them from the target directory: $targetDir
5. Produce a complete compatibility assessment following your output schema.

## Important

- Run the build command to verify CLI buildability.
- Run the test command to verify test suite health.
- Read configuration files (appsettings.json, Program.cs, Startup.cs, etc.) to detect endpoints, health checks, database config.
- If .hone/ already exists, validate it against the adapter contract.
- Produce ONLY the JSON output. No other text.
"@

# ── Invoke agent ────────────────────────────────────────────────────────────
Write-Information 'Running compatibility assessment agent...' -InformationAction Continue

$agentParams = @{
    AgentName = 'hone-compatibility'
    Prompt = $prompt
    ModelConfigKey = 'AnalysisModel'
    DefaultModel = 'claude-opus-4.6'
    SpinnerMessage = 'Assessing target compatibility'
    CompletionMessage = 'Compatibility assessment complete'
    ResponsePath = Join-Path (Split-Path $OutputPath) 'compatibility-response.txt'
    MaxRetries = 1
    RetryPromptSuffix = 'Your previous response was not valid JSON. Please respond with ONLY a JSON object matching the output schema.'
}
if ($ConfigPath) {
    $agentParams.ConfigPath = $ConfigPath
}
if ($Model) {
    $agentParams.DefaultModel = $Model
}
$agentParams.WorkingDirectory = $targetDir

$result = & (Join-Path $harnessRoot 'Invoke-CopilotAgent.ps1') @agentParams

if (-not $result.Success) {
    $errorDetail = if ($result.TimedOut) { 'Agent timed out' } else { "Agent failed (exit code $($result.ExitCode))" }
    Write-Warning "Compatibility assessment failed: $errorDetail"
    return [PSCustomObject]@{
        Success = $false
        Message = $errorDetail
        Report = $null
        OutputPath = $null
    }
}

if (-not $result.ParsedJson) {
    Write-Warning 'Agent response was not valid JSON. Raw response saved.'
    return [PSCustomObject]@{
        Success = $false
        Message = 'Agent response was not valid JSON'
        Report = $null
        OutputPath = $agentParams.ResponsePath
    }
}

$report = $result.ParsedJson

# ── Write report ────────────────────────────────────────────────────────────
$outputDir = Split-Path -Parent $OutputPath
if ($outputDir -and -not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}
$result.ResponseText | Out-File -FilePath $OutputPath -Encoding utf8
Write-Information "Assessment report written to: $OutputPath" -InformationAction Continue

# ── Display summary ─────────────────────────────────────────────────────────
$compatibility = $report.compatibility
$overall = if ($compatibility.overall) { $compatibility.overall.ToUpper() } else { 'UNKNOWN' }
$score = if ($null -ne $compatibility.score) { "$($compatibility.score)/100" } else { 'N/A' }

Write-Information '' -InformationAction Continue
Write-Information "═══════════════════════════════════════════════════" -InformationAction Continue
Write-Information "  COMPATIBILITY: $overall  ($score)" -InformationAction Continue
Write-Information "═══════════════════════════════════════════════════" -InformationAction Continue

if ($report.target) {
    $stack = if ($report.target.detectedStack) { $report.target.detectedStack } else { 'unknown' }
    $framework = if ($report.target.detectedFramework) { $report.target.detectedFramework } else { 'unknown' }
    Write-Information "  Stack: $stack ($framework)" -InformationAction Continue
}

if ($compatibility.blockers -and $compatibility.blockers.Count -gt 0) {
    Write-Information '' -InformationAction Continue
    Write-Information '  BLOCKERS:' -InformationAction Continue
    foreach ($blocker in $compatibility.blockers) {
        Write-Information "    X  [$($blocker.area)] $($blocker.issue)" -InformationAction Continue
        Write-Information "       Fix: $($blocker.remediation)" -InformationAction Continue
    }
}

if ($compatibility.warnings -and $compatibility.warnings.Count -gt 0) {
    Write-Information '' -InformationAction Continue
    Write-Information '  WARNINGS:' -InformationAction Continue
    foreach ($warning in $compatibility.warnings) {
        Write-Information "    !  [$($warning.area)] $($warning.issue)" -InformationAction Continue
        Write-Information "       Fix: $($warning.remediation)" -InformationAction Continue
    }
}

if ($compatibility.ready -and $compatibility.ready.Count -gt 0) {
    Write-Information '' -InformationAction Continue
    Write-Information '  READY:' -InformationAction Continue
    foreach ($readyItem in $compatibility.ready) {
        Write-Information "    OK [$($readyItem.area)] $($readyItem.detail)" -InformationAction Continue
    }
}

if ($report.onboardingPlan -and $report.onboardingPlan.summary) {
    Write-Information '' -InformationAction Continue
    Write-Information "  SUMMARY: $($report.onboardingPlan.summary)" -InformationAction Continue
}

Write-Information "═══════════════════════════════════════════════════" -InformationAction Continue
Write-Information '' -InformationAction Continue

# ── Return structured result ────────────────────────────────────────────────
return [PSCustomObject]@{
    Success = $true
    Message = "Assessment complete: $overall ($score)"
    Report = $report
    OutputPath = $OutputPath
}
