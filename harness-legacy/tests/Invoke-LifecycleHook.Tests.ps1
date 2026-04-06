BeforeAll {
    Import-Module (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'HoneHelpers.psm1') -Force
    $fixtureDir = Join-Path -Path $PSScriptRoot -ChildPath 'fixtures' -AdditionalChildPath 'mock-target'
    $harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $targetConfig = Import-PowerShellDataFile (Join-Path -Path $fixtureDir -ChildPath '.hone' -AdditionalChildPath 'config.psd1')
    $mockConfig = @{ Api = @{ HealthEndpoint = '/health' } }
    $null = $harnessRoot, $targetConfig, $mockConfig
}

Describe 'Invoke-LifecycleHook' {
    It 'executes Script hook and returns success' {
        $result = Invoke-LifecycleHook -Name 'Prepare' -TargetConfig $targetConfig -TargetDir $fixtureDir -HarnessRoot $harnessRoot -Config $mockConfig
        $result.Success | Should -BeTrue
        $result.Message | Should -BeLike '*Mock*'
    }

    It 'executes Skip hook and returns success immediately' {
        $result = Invoke-LifecycleHook -Name 'Ready' -TargetConfig $targetConfig -TargetDir $fixtureDir -HarnessRoot $harnessRoot -Config $mockConfig
        $result.Success | Should -BeTrue
        $result.Message | Should -Be 'Skipped'
    }
}
