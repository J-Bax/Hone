BeforeAll {
    $harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $scriptPath = Join-Path -Path $harnessRoot -ChildPath 'Compare-Results.ps1'
    $configPath = Join-Path -Path $harnessRoot -ChildPath 'config.psd1'
    $originalLogPath = $env:HONE_LOG_PATH

    function Get-TestMetric {
        param(
            [double]$P95,
            [double]$Rps,
            [double]$ErrorRate
        )

        return [PSCustomObject]@{
            HttpReqDuration = [PSCustomObject]@{ P95 = $P95 }
            HttpReqs = [PSCustomObject]@{ Rate = $Rps }
            HttpReqFailed = [PSCustomObject]@{ Rate = $ErrorRate }
        }
    }

    function Get-TestCounter {
        param(
            [double]$CpuAvg,
            [double]$CpuMax,
            [double]$WorkingSetAvg,
            [double]$WorkingSetMax
        )

        return [PSCustomObject]@{
            Runtime = [PSCustomObject]@{
                CpuUsage = [PSCustomObject]@{ Avg = $CpuAvg; Max = $CpuMax }
                GcHeapSizeMB = [PSCustomObject]@{ Avg = 64; Max = 80 }
                Gen0Collections = [PSCustomObject]@{ Last = 1 }
                Gen1Collections = [PSCustomObject]@{ Last = 0 }
                Gen2Collections = [PSCustomObject]@{ Last = 0 }
                GcPauseRatio = [PSCustomObject]@{ Avg = 0.01; Max = 0.02 }
                ThreadPoolThreads = [PSCustomObject]@{ Avg = 8; Max = 12 }
                ThreadPoolQueue = [PSCustomObject]@{ Avg = 0; Max = 1 }
                ExceptionCount = [PSCustomObject]@{ Last = 0 }
                WorkingSetMB = [PSCustomObject]@{ Avg = $WorkingSetAvg; Max = $WorkingSetMax }
                AllocRateMB = [PSCustomObject]@{ Avg = 12; Max = 15 }
            }
        }
    }

    $null = $harnessRoot, $scriptPath, $configPath, $originalLogPath
}

AfterAll {
    $env:HONE_LOG_PATH = $originalLogPath
}

Describe 'Compare-Results acceptance policy' {
    It 'treats flat metrics as stale when nothing changed' {
        $env:HONE_LOG_PATH = Join-Path -Path $TestDrive -ChildPath 'compare-stale.jsonl'
        $baseline = Get-TestMetric -P95 100 -Rps 50 -ErrorRate 0.01

        $result = & $scriptPath `
            -CurrentMetrics $baseline `
            -BaselineMetrics $baseline `
            -PreviousMetrics $baseline `
            -ConfigPath $configPath `
            -Experiment 1

        $result.Improved | Should -BeFalse
        $result.Regression | Should -BeFalse
        $result.TiebreakerUsed | Should -BeFalse
        $result.ImprovementPct | Should -Be 0
        $result.RegressionDetail | Should -Be ''
    }

    It 'flags meaningful latency regressions' {
        $env:HONE_LOG_PATH = Join-Path -Path $TestDrive -ChildPath 'compare-regression.jsonl'
        $baseline = Get-TestMetric -P95 100 -Rps 50 -ErrorRate 0.01
        $current = Get-TestMetric -P95 120 -Rps 50 -ErrorRate 0.01

        $result = & $scriptPath `
            -CurrentMetrics $current `
            -BaselineMetrics $baseline `
            -PreviousMetrics $baseline `
            -ConfigPath $configPath `
            -Experiment 2

        $result.Improved | Should -BeFalse
        $result.Regression | Should -BeTrue
        $result.RegressionDetail | Should -Match 'p95 regressed'
    }

    It 'uses the efficiency tiebreaker when metrics are flat but resource use drops' {
        $env:HONE_LOG_PATH = Join-Path -Path $TestDrive -ChildPath 'compare-efficiency.jsonl'
        $baseline = Get-TestMetric -P95 100 -Rps 50 -ErrorRate 0.01
        $currentCounters = Get-TestCounter -CpuAvg 10 -CpuMax 14 -WorkingSetAvg 80 -WorkingSetMax 100
        $previousCounters = Get-TestCounter -CpuAvg 20 -CpuMax 24 -WorkingSetAvg 120 -WorkingSetMax 150

        $result = & $scriptPath `
            -CurrentMetrics $baseline `
            -BaselineMetrics $baseline `
            -PreviousMetrics $baseline `
            -CurrentCounterMetrics $currentCounters `
            -PreviousCounterMetrics $previousCounters `
            -ConfigPath $configPath `
            -Experiment 3

        $result.Improved | Should -BeTrue
        $result.Regression | Should -BeFalse
        $result.EfficiencyImproved | Should -BeTrue
        $result.TiebreakerUsed | Should -BeTrue
        $result.EfficiencyDeltas.CpuUsage.Improved | Should -BeTrue
        $result.EfficiencyDeltas.WorkingSet.Improved | Should -BeTrue
    }

    It 'does not flag latency regression when percent worsens but absolute delta stays below threshold' {
        $env:HONE_LOG_PATH = Join-Path -Path $TestDrive -ChildPath 'compare-latency-absolute-threshold.jsonl'
        $baseline = Get-TestMetric -P95 40 -Rps 50 -ErrorRate 0.01
        $current = Get-TestMetric -P95 44.2 -Rps 50 -ErrorRate 0.01

        $result = & $scriptPath `
            -CurrentMetrics $current `
            -BaselineMetrics $baseline `
            -PreviousMetrics $baseline `
            -ConfigPath $configPath `
            -Experiment 4

        $result.Regression | Should -BeFalse
        $result.Deltas.P95Latency.Regressed | Should -BeFalse
        $result.RegressionDetail | Should -Be ''
    }

    It 'does not flag throughput regression when percent worsens but absolute delta stays below threshold' {
        $env:HONE_LOG_PATH = Join-Path -Path $TestDrive -ChildPath 'compare-rps-absolute-threshold.jsonl'
        $baseline = Get-TestMetric -P95 100 -Rps 30 -ErrorRate 0.01
        $current = Get-TestMetric -P95 100 -Rps 26.5 -ErrorRate 0.01

        $result = & $scriptPath `
            -CurrentMetrics $current `
            -BaselineMetrics $baseline `
            -PreviousMetrics $baseline `
            -ConfigPath $configPath `
            -Experiment 5

        $result.Regression | Should -BeFalse
        $result.Deltas.RPS.Regressed | Should -BeFalse
        $result.RegressionDetail | Should -Be ''
    }

    It 'marks mixed-signal runs as regressions when a gated metric regresses' {
        $env:HONE_LOG_PATH = Join-Path -Path $TestDrive -ChildPath 'compare-mixed-signal.jsonl'
        $baseline = Get-TestMetric -P95 100 -Rps 50 -ErrorRate 0.01
        $current = Get-TestMetric -P95 90 -Rps 50 -ErrorRate 0.03

        $result = & $scriptPath `
            -CurrentMetrics $current `
            -BaselineMetrics $baseline `
            -PreviousMetrics $baseline `
            -ConfigPath $configPath `
            -Experiment 6

        $result.Improved | Should -BeTrue
        $result.Regression | Should -BeTrue
        $result.Deltas.P95Latency.Improved | Should -BeTrue
        $result.Deltas.ErrorRate.Regressed | Should -BeTrue
        $result.RegressionDetail | Should -Match 'Error rate regressed'
    }

    It 'never uses the efficiency tiebreaker to override a real regression' {
        $env:HONE_LOG_PATH = Join-Path -Path $TestDrive -ChildPath 'compare-no-tiebreaker-on-regression.jsonl'
        $baseline = Get-TestMetric -P95 100 -Rps 50 -ErrorRate 0.01
        $current = Get-TestMetric -P95 120 -Rps 50 -ErrorRate 0.01
        $currentCounters = Get-TestCounter -CpuAvg 10 -CpuMax 14 -WorkingSetAvg 80 -WorkingSetMax 100
        $previousCounters = Get-TestCounter -CpuAvg 20 -CpuMax 24 -WorkingSetAvg 120 -WorkingSetMax 150

        $result = & $scriptPath `
            -CurrentMetrics $current `
            -BaselineMetrics $baseline `
            -PreviousMetrics $baseline `
            -CurrentCounterMetrics $currentCounters `
            -PreviousCounterMetrics $previousCounters `
            -ConfigPath $configPath `
            -Experiment 7

        $result.Regression | Should -BeTrue
        $result.EfficiencyImproved | Should -BeTrue
        $result.TiebreakerUsed | Should -BeFalse
    }
}
