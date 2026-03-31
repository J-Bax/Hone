function New-HoneTestTarget {
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$TargetDir,

        [string]$MetadataPath = 'results\metadata',

        [string]$ResultsPath = 'results'
    )

    if (-not $PSCmdlet.ShouldProcess($TargetDir, 'Create Hone test target fixture')) {
        return $TargetDir
    }

    $honeDir = Join-Path -Path $TargetDir -ChildPath '.hone'
    $hooksDir = Join-Path -Path $honeDir -ChildPath 'hooks'
    $scenariosDir = Join-Path -Path $honeDir -ChildPath 'scenarios'
    $fixturesDir = Join-Path -Path $honeDir -ChildPath 'fixtures'
    $projectDir = Join-Path -Path $TargetDir -ChildPath 'MockApi'
    $controllersDir = Join-Path -Path $projectDir -ChildPath 'Controllers'
    $resultsDir = Join-Path -Path $TargetDir -ChildPath $ResultsPath
    $metadataDir = Join-Path -Path $TargetDir -ChildPath $MetadataPath

    foreach ($dir in @($hooksDir, $scenariosDir, $fixturesDir, $controllersDir, $resultsDir, $metadataDir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    'Microsoft Visual Studio Solution File, Format Version 12.00' |
        Set-Content -Path (Join-Path -Path $TargetDir -ChildPath 'MockApi.sln') -Encoding ascii

    @'
namespace MockApi.Controllers;

public class ProductsController
{
}
'@ | Set-Content -Path (Join-Path -Path $controllersDir -ChildPath 'ProductsController.cs') -Encoding ascii

    @'
export default function () {}
'@ | Set-Content -Path (Join-Path -Path $scenariosDir -ChildPath 'baseline.js') -Encoding ascii

    @'
export default function () {}
'@ | Set-Content -Path (Join-Path -Path $scenariosDir -ChildPath 'secondary.js') -Encoding ascii

    @'
{
  "scenarios": {
    "baseline": {
      "description": "Mock baseline scenario",
      "file": "baseline.js",
      "use_for_optimization": true
    },
    "secondary": {
      "description": "Mock secondary scenario",
      "file": "secondary.js",
      "use_for_optimization": false
    }
  }
}
'@ | Set-Content -Path (Join-Path -Path $scenariosDir -ChildPath 'thresholds.json') -Encoding ascii

    @'
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$TargetPath,
    [Parameter(Mandatory)] [hashtable]$Config,
    [string]$BaseUrl,
    [string]$Experiment
)

$null = $TargetPath, $Config, $BaseUrl, $Experiment

[PSCustomObject]@{
    Success = $true
    Message = 'Mock prepare completed'
    Duration = [timespan]::Zero
    Artifacts = @()
}
'@ | Set-Content -Path (Join-Path -Path $hooksDir -ChildPath 'prepare.ps1') -Encoding ascii

    @"
@{
    Name       = 'MockTarget'
    BaseBranch = 'main'

    Api = @{
        SolutionPath     = 'MockApi.sln'
        ProjectPath      = 'MockApi'
        TestProjectPath  = 'MockApi.sln'
        MetadataPath     = '$MetadataPath'
        ResultsPath      = '$ResultsPath'
        BaseUrl          = 'http://localhost:0'
        HealthEndpoint   = '/health'
        GcEndpoint       = '/diag/gc'
        StartupTimeout   = 10
        SourceCodePaths  = @('Controllers')
        SourceFileGlob   = '*.cs'
    }

    Hooks = @{
        Prepare  = @{ Type = 'Script'; Path = '.hone\hooks\prepare.ps1' }
        Start    = @{ Type = 'Shared'; Name = 'dotnet-start' }
        Stop     = @{ Type = 'Shared'; Name = 'dotnet-stop' }
        Ready    = @{ Type = 'Skip' }
        Warmup   = @{ Type = 'Skip' }
        Active   = @{ Type = 'Shared'; Name = 'k6-run' }
        Cooldown = @{ Type = 'Http'; Method = 'POST'; Path = '/diag/gc' }
        Cleanup  = @{ Type = 'Skip' }
    }

    ScaleTest = @{
        ScenarioPath         = '.hone\scenarios\baseline.js'
        ScenarioRegistryPath = '.hone\scenarios\thresholds.json'
        MeasuredRuns         = 1
        WarmupEnabled        = `$false
        CooldownSeconds      = 0
    }
}
"@ | Set-Content -Path (Join-Path -Path $honeDir -ChildPath 'config.psd1') -Encoding ascii

    return $TargetDir
}

function Enable-HarnessTestingFixture {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$TargetDir,

        [Parameter(Mandatory)]
        [string]$FixtureManifestContent
    )

    $configPath = Join-Path -Path (Join-Path -Path $TargetDir -ChildPath '.hone') -ChildPath 'config.psd1'
    $configContent = Get-Content -Path $configPath -Raw

    if ($configContent -notmatch 'HarnessTesting') {
        $configContent = $configContent -replace "\r?\n\}\s*$", @"

    HarnessTesting = @{
        Enabled      = `$true
        ManifestPath = '.hone\fixtures\fixture.psd1'
    }
}
"@
    }

    Set-Content -Path $configPath -Value $configContent -Encoding ascii
    $FixtureManifestContent | Set-Content -Path (Join-Path -Path $TargetDir -ChildPath '.hone\fixtures\fixture.psd1') -Encoding ascii
}

function Get-HoneFixtureAssetPath {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$RelativePath
    )

    $harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    return (Join-Path -Path $harnessRoot -ChildPath "test-fixtures\$RelativePath")
}

function Get-HoneTargetLayout {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]$TargetDir
    )

    $resultsPath = 'results'
    $metadataPath = 'results\metadata'
    $configPath = Join-Path -Path (Join-Path -Path $TargetDir -ChildPath '.hone') -ChildPath 'config.psd1'

    if (Test-Path -Path $configPath) {
        $targetConfig = Import-PowerShellDataFile -Path $configPath
        if ($targetConfig.ContainsKey('Api') -and $targetConfig.Api) {
            if ($targetConfig.Api.ContainsKey('ResultsPath') -and $targetConfig.Api.ResultsPath) {
                $resultsPath = $targetConfig.Api.ResultsPath
            }

            if ($targetConfig.Api.ContainsKey('MetadataPath') -and $targetConfig.Api.MetadataPath) {
                $metadataPath = $targetConfig.Api.MetadataPath
            } else {
                $metadataPath = Join-Path -Path $resultsPath -ChildPath 'metadata'
            }
        }
    }

    return [pscustomobject]@{
        ResultsPath = $resultsPath
        MetadataPath = $metadataPath
        ResultsDir = Join-Path -Path $TargetDir -ChildPath $resultsPath
        MetadataDir = Join-Path -Path $TargetDir -ChildPath $metadataPath
    }
}

function Get-HoneTargetFixturePath {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $fixturesRoot = Join-Path -Path $PSScriptRoot -ChildPath 'fixtures\targets'
    $fixturePath = Join-Path -Path $fixturesRoot -ChildPath $Name
    if (-not (Test-Path -Path $fixturePath)) {
        throw "Harness target fixture not found: $fixturePath"
    }

    return $fixturePath
}

function Reset-HoneFixtureRun {
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]$TargetDir
    )

    $layout = Get-HoneTargetLayout -TargetDir $TargetDir
    if (-not $PSCmdlet.ShouldProcess($TargetDir, 'Reset staged fixture run state')) {
        return [pscustomobject]@{
            ResultsDir = $layout.ResultsDir
            MetadataDir = $layout.MetadataDir
            RemovedPaths = @()
        }
    }

    foreach ($dir in @($layout.ResultsDir, $layout.MetadataDir)) {
        if (-not (Test-Path -Path $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
    }

    $removedPaths = [System.Collections.Generic.List[string]]::new()

    foreach ($artifactPath in @(
            (Join-Path -Path $layout.ResultsDir -ChildPath 'baseline.json'),
            (Join-Path -Path $layout.ResultsDir -ChildPath 'run-metadata.json'),
            (Join-Path -Path $layout.ResultsDir -ChildPath 'hone.jsonl')
        )) {
        if (Test-Path -Path $artifactPath) {
            Remove-Item -Path $artifactPath -Force
            $removedPaths.Add($artifactPath)
        }
    }

    Get-ChildItem -Path $layout.ResultsDir -Filter 'baseline-*.json' -ErrorAction SilentlyContinue |
        ForEach-Object {
            Remove-Item -Path $_.FullName -Force
            $removedPaths.Add($_.FullName)
        }

    Get-ChildItem -Path $layout.ResultsDir -Filter 'experiment-*' -ErrorAction SilentlyContinue |
        ForEach-Object {
            Remove-Item -Path $_.FullName -Recurse -Force
            $removedPaths.Add($_.FullName)
        }

    foreach ($metadataFile in @('experiment-log.md', 'experiment-queue.json', 'experiment-queue.md')) {
        $metadataPath = Join-Path -Path $layout.MetadataDir -ChildPath $metadataFile
        if (Test-Path -Path $metadataPath) {
            Remove-Item -Path $metadataPath -Force
            $removedPaths.Add($metadataPath)
        }
    }

    $rootCauseDir = Join-Path -Path $layout.MetadataDir -ChildPath 'root-causes'
    if (Test-Path -Path $rootCauseDir) {
        Remove-Item -Path $rootCauseDir -Recurse -Force
        $removedPaths.Add($rootCauseDir)
    }

    return [pscustomobject]@{
        ResultsDir = $layout.ResultsDir
        MetadataDir = $layout.MetadataDir
        RemovedPaths = @($removedPaths)
    }
}

function Copy-HoneTargetFixture {
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$DestinationPath
    )

    $sourcePath = Get-HoneTargetFixturePath -Name $Name
    if (-not $PSCmdlet.ShouldProcess($DestinationPath, "Stage target fixture '$Name'")) {
        return $DestinationPath
    }

    $destinationParent = Split-Path -Path $DestinationPath -Parent
    if ($destinationParent -and -not (Test-Path -Path $destinationParent)) {
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    }

    if (Test-Path -Path $DestinationPath) {
        Remove-Item -Path $DestinationPath -Recurse -Force
    }

    Copy-Item -Path $sourcePath -Destination $DestinationPath -Recurse -Force
    Reset-HoneFixtureRun -TargetDir $DestinationPath -Confirm:$false | Out-Null

    return $DestinationPath
}

function Initialize-HoneTargetRepository {
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$TargetDir,

        [string]$BranchName = 'main'
    )

    if (-not $PSCmdlet.ShouldProcess($TargetDir, "Initialize git repository ($BranchName)")) {
        return $TargetDir
    }

    Push-Location $TargetDir
    try {
        git init 2>&1 | Out-Null
        git config user.name 'Hone Fixture Tests' 2>&1 | Out-Null
        git config user.email 'hone-tests@example.invalid' 2>&1 | Out-Null
        git add . 2>&1 | Out-Null
        git commit --no-gpg-sign -m 'fixture baseline' 2>&1 | Out-Null
        git branch -M $BranchName 2>&1 | Out-Null
    } finally {
        Pop-Location
    }

    return $TargetDir
}

function Set-HoneFixtureBaseline {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)]
        [string]$TargetDir,

        [string]$ResultsPath = 'results',

        [string]$BaselineSummaryAsset = 'k6-results\baseline-summary.json',

        [string]$CounterMetricsAsset = 'diagnostics\runtime-counters.json',

        [hashtable]$ScenarioBaselineAssets = @{
            secondary = 'k6-results\baseline-secondary-summary.json'
        }
    )

    $resolvedResultsPath = if ($PSBoundParameters.ContainsKey('ResultsPath')) {
        $ResultsPath
    } else {
        (Get-HoneTargetLayout -TargetDir $TargetDir).ResultsPath
    }

    $resultsDir = Join-Path -Path $TargetDir -ChildPath $resolvedResultsPath
    if (-not $PSCmdlet.ShouldProcess($TargetDir, 'Seed deterministic baseline artifacts')) {
        return [PSCustomObject]@{
            BaselinePath = Join-Path -Path $resultsDir -ChildPath 'baseline.json'
            CounterBaselinePath = Join-Path -Path $resultsDir -ChildPath 'baseline-counters.json'
        }
    }

    if (-not (Test-Path -Path $resultsDir)) {
        New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
    }

    $baselineAssetPath = Get-HoneFixtureAssetPath -RelativePath $BaselineSummaryAsset
    $baselineSummary = Get-Content -Path $baselineAssetPath -Raw | ConvertFrom-Json

    $baselineMetrics = [ordered]@{
        Timestamp = '2026-01-01T00:00:00.0000000Z'
        Experiment = 0
        HttpReqDuration = [ordered]@{
            Avg = $baselineSummary.metrics.http_req_duration.avg
            P50 = $baselineSummary.metrics.http_req_duration.med
            P90 = $baselineSummary.metrics.http_req_duration.'p(90)'
            P95 = $baselineSummary.metrics.http_req_duration.'p(95)'
            P99 = $baselineSummary.metrics.http_req_duration.'p(99)'
            Max = $baselineSummary.metrics.http_req_duration.max
        }
        HttpReqs = [ordered]@{
            Count = $baselineSummary.metrics.http_reqs.count
            Rate = $baselineSummary.metrics.http_reqs.rate
        }
        HttpReqFailed = [ordered]@{
            Count = [int]$baselineSummary.metrics.http_req_failed.passes
            Rate = $baselineSummary.metrics.http_req_failed.value
        }
    }

    $baselineMetrics | ConvertTo-Json -Depth 10 | Set-Content -Path (Join-Path -Path $resultsDir -ChildPath 'baseline.json') -Encoding utf8

    if ($CounterMetricsAsset) {
        Copy-Item -Path (Get-HoneFixtureAssetPath -RelativePath $CounterMetricsAsset) `
            -Destination (Join-Path -Path $resultsDir -ChildPath 'baseline-counters.json') -Force
    }

    foreach ($scenarioName in $ScenarioBaselineAssets.Keys) {
        Copy-Item -Path (Get-HoneFixtureAssetPath -RelativePath $ScenarioBaselineAssets[$scenarioName]) `
            -Destination (Join-Path -Path $resultsDir -ChildPath "baseline-$scenarioName.json") -Force
    }

    [PSCustomObject]@{
        BaselinePath = Join-Path -Path $resultsDir -ChildPath 'baseline.json'
        CounterBaselinePath = Join-Path -Path $resultsDir -ChildPath 'baseline-counters.json'
    }
}

function Assert-HoneArtifactCategory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$TargetDir,

        [Parameter(Mandatory)]
        [int]$Experiment,

        [string[]]$Categories
    )

    $layout = Get-HoneTargetLayout -TargetDir $TargetDir
    $resultsDir = $layout.ResultsDir
    $experimentDir = Join-Path -Path $resultsDir -ChildPath "experiment-$Experiment"

    $categoryChecks = @{
        analysis_prompt = { Test-Path -Path (Join-Path -Path $experimentDir -ChildPath 'analysis-prompt.md') | Should -BeTrue }
        analysis_response = { Test-Path -Path (Join-Path -Path $experimentDir -ChildPath 'analysis-response.json') | Should -BeTrue }
        classification_response = { Test-Path -Path (Join-Path -Path $experimentDir -ChildPath 'classification-response.json') | Should -BeTrue }
        fix_response = { Test-Path -Path (Join-Path -Path $experimentDir -ChildPath 'fix-response.md') | Should -BeTrue }
        root_cause = { Test-Path -Path (Join-Path -Path $experimentDir -ChildPath 'root-cause.md') | Should -BeTrue }
        build_output = { Test-Path -Path (Join-Path -Path $experimentDir -ChildPath 'build.log') | Should -BeTrue }
        e2e_output = { Test-Path -Path (Join-Path -Path $experimentDir -ChildPath 'e2e-tests.log') | Should -BeTrue }
        e2e_trx = { Test-Path -Path (Join-Path -Path $experimentDir -ChildPath 'e2e-results.trx') | Should -BeTrue }
        k6_summary = { Test-Path -Path (Join-Path -Path $experimentDir -ChildPath 'k6-summary.json') | Should -BeTrue }
        k6_log = { Test-Path -Path (Join-Path -Path $experimentDir -ChildPath 'k6.log') | Should -BeTrue }
        counter_metrics = { Test-Path -Path (Join-Path -Path $experimentDir -ChildPath 'dotnet-counters.json') | Should -BeTrue }
        diagnostic_reports = {
            @(Get-ChildItem -Path (Join-Path -Path $experimentDir -ChildPath 'diagnostics') -Recurse -File -Filter '*.json' -ErrorAction SilentlyContinue).Count |
                Should -BeGreaterThan 0
        }
        run_metadata = { Test-Path -Path (Join-Path -Path $resultsDir -ChildPath 'run-metadata.json') | Should -BeTrue }
        queue_state = { Test-Path -Path (Join-Path -Path $layout.MetadataDir -ChildPath 'experiment-queue.json') | Should -BeTrue }
        hone_log = { Test-Path -Path (Join-Path -Path $resultsDir -ChildPath 'hone.jsonl') | Should -BeTrue }
        baseline_metrics = { Test-Path -Path (Join-Path -Path $resultsDir -ChildPath 'baseline.json') | Should -BeTrue }
        baseline_counter_metrics = { Test-Path -Path (Join-Path -Path $resultsDir -ChildPath 'baseline-counters.json') | Should -BeTrue }
        scenario_baselines = {
            $scenarioBaselineCount = @(
                Get-ChildItem -Path $resultsDir -File -Filter 'baseline-*.json' -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -ne 'baseline-counters.json' }
            ).Count

            $scenarioBaselineCount | Should -BeGreaterThan 0
        }
        root_cause_docs = { Test-Path -Path (Join-Path -Path $layout.MetadataDir -ChildPath 'root-causes') | Should -BeTrue }
    }

    foreach ($category in $Categories) {
        if (-not $categoryChecks.ContainsKey($category)) {
            throw "Unknown artifact category: $category"
        }

        & $categoryChecks[$category]
    }
}
