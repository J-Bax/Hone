BeforeAll {
    . (Join-Path -Path $PSScriptRoot -ChildPath 'TestHelpers.ps1')
    $harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    Import-Module (Join-Path -Path $harnessRoot -ChildPath 'HoneHelpers.psm1') -Force
    $repoRoot = Split-Path -Parent $harnessRoot
    $scriptPath = Join-Path -Path $harnessRoot -ChildPath 'Build-AnalysisContext.ps1'
    $configPath = Join-Path -Path $harnessRoot -ChildPath 'config.psd1'
    $null = $harnessRoot, $repoRoot, $scriptPath, $configPath
}

Describe 'Build-AnalysisContext target-aware behavior' {
    It 'loads source, scenario, metadata, and diagnostics from the target directory' {
        $targetDir = New-HoneTestTarget -TargetDir (Join-Path -Path $TestDrive -ChildPath 'context-target') -MetadataPath 'artifacts\metadata' -ResultsPath 'artifacts'
        $controllersDir = Join-Path -Path $targetDir -ChildPath 'MockApi\Controllers'
        $metadataDir = Join-Path -Path $targetDir -ChildPath 'artifacts\metadata'
        $resultsDir = Join-Path -Path $targetDir -ChildPath 'artifacts'

        New-Item -ItemType Directory -Path $controllersDir -Force | Out-Null
        New-Item -ItemType Directory -Path $metadataDir -Force | Out-Null
        New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null

        @'
namespace MockApi.Controllers;
public class ProductsController {}
'@ | Set-Content -Path (Join-Path -Path $controllersDir -ChildPath 'ProductsController.cs') -Encoding ascii

        '## Prior Optimizations' | Set-Content -Path (Join-Path -Path $metadataDir -ChildPath 'experiment-log.md') -Encoding ascii

        $queue = [PSCustomObject]@{
            items = @(
                [PSCustomObject]@{
                    filePath = 'MockApi\Controllers\ProductsController.cs'
                    explanation = 'Optimize controller query'
                    status = 'pending'
                    scope = 'narrow'
                },
                [PSCustomObject]@{
                    filePath = 'MockApi\Data\ProductRepository.cs'
                    explanation = 'Already attempted'
                    status = 'done'
                    scope = 'architecture'
                    triedByExperiment = 2
                    outcome = 'stale'
                }
            )
        }
        $queue | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path -Path $metadataDir -ChildPath 'experiment-queue.json') -Encoding ascii

        $runMetadata = [PSCustomObject]@{
            Experiments = @(
                [PSCustomObject]@{
                    Experiment = 1
                    Outcome = 'improved'
                    P95 = 118.4
                    RPS = 91.2
                    BranchName = 'hone/experiment-1'
                }
            )
        }
        $runMetadata | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path -Path $resultsDir -ChildPath 'run-metadata.json') -Encoding ascii

        $engineConfig = Import-PowerShellDataFile -Path $configPath
        $targetConfig = Import-PowerShellDataFile -Path (Join-Path -Path $targetDir -ChildPath '.hone\config.psd1')
        $mergedConfig = Merge-HoneConfig -Engine $engineConfig -Target $targetConfig

        $counterMetrics = [PSCustomObject]@{
            Runtime = [PSCustomObject]@{
                CpuUsage = [PSCustomObject]@{ Avg = 12.3 }
                GcHeapSizeMB = [PSCustomObject]@{ Max = 45.6 }
                Gen2Collections = [PSCustomObject]@{ Last = 2 }
                ThreadPoolThreads = [PSCustomObject]@{ Max = 18 }
            }
        }

        $diagnosticReports = @{
            'cpu-hotspots' = @{
                Report = [PSCustomObject]@{
                    hottestMethods = @('ProductsController.GetAll')
                }
                Summary = 'Controller query is hot'
            }
        }

        $result = & $scriptPath `
            -Config $mergedConfig `
            -RepoRoot $repoRoot `
            -TargetDir $targetDir `
            -CounterMetrics $counterMetrics `
            -PreviousRcaExplanation 'Cache the product list' `
            -DiagnosticReports $diagnosticReports

        $result.SourceFilePaths | Should -Contain 'MockApi\Controllers\ProductsController.cs'
        $result.CounterContext | Should -Match 'CPU avg: 12.3%'
        $result.TrafficContext | Should -Match 'export default function'
        $result.HistoryContext | Should -Match 'Previously Tried Optimizations'
        $result.HistoryContext | Should -Match 'Known Optimization Queue'
        $result.HistoryContext | Should -Match 'MockApi\\Controllers\\ProductsController.cs'
        $result.HistoryContext | Should -Match 'Cache the product list'
        $result.HistoryContext | Should -Match 'Experiment History'
        $result.HistoryContext | Should -Match 'hone/experiment-1'
        $result.ProfilingContext | Should -Match 'cpu-hotspots'
        $result.ProfilingContext | Should -Match 'ProductsController.GetAll'
    }
}
