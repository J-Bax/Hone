BeforeAll {
    $harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $configPath = Join-Path -Path $harnessRoot -ChildPath 'config.psd1'
    $fixtureDir = Join-Path -Path $PSScriptRoot -ChildPath 'fixtures' -AdditionalChildPath 'mock-target'
    $null = $harnessRoot, $configPath, $fixtureDir
}

Describe 'Test-HoneConfig target validation' {
    It 'accepts a valid mock target config' {
        {
            & (Join-Path -Path $harnessRoot -ChildPath 'Test-HoneConfig.ps1') `
                -ConfigPath $configPath `
                -TargetPath $fixtureDir
        } | Should -Not -Throw
    }

    It 'rejects a missing shared hook implementation' {
        $tempTarget = Join-Path -Path $TestDrive -ChildPath 'missing-shared'
        Copy-Item -Path $fixtureDir -Destination $tempTarget -Recurse
        $cfgPath = Join-Path -Path $tempTarget -ChildPath '.hone' -AdditionalChildPath 'config.psd1'
        $content = Get-Content -Path $cfgPath -Raw
        $content = $content -replace "Name = 'dotnet-stop'", "Name = 'missing-shared-hook'"
        Set-Content -Path $cfgPath -Value $content -Encoding utf8

        {
            & (Join-Path -Path $harnessRoot -ChildPath 'Test-HoneConfig.ps1') `
                -ConfigPath $configPath `
                -TargetPath $tempTarget
        } | Should -Throw '*shared hook not found*'
    }

    It 'rejects missing warmup scenario when warmup is enabled' {
        $tempTarget = Join-Path -Path $TestDrive -ChildPath 'missing-warmup'
        Copy-Item -Path $fixtureDir -Destination $tempTarget -Recurse
        $cfgPath = Join-Path -Path $tempTarget -ChildPath '.hone' -AdditionalChildPath 'config.psd1'
        $content = Get-Content -Path $cfgPath -Raw
        $content = $content -replace 'WarmupEnabled\s*=\s*\$false', 'WarmupEnabled = $true'
        $content = $content -replace "ScenarioRegistryPath = '.hone\\scenarios\\thresholds.json'\r?\n", "ScenarioRegistryPath = '.hone\\scenarios\\thresholds.json'`r`n        WarmupScenarioPath   = '.hone\\scenarios\\missing-warmup.js'`r`n"
        Set-Content -Path $cfgPath -Value $content -Encoding utf8

        {
            & (Join-Path -Path $harnessRoot -ChildPath 'Test-HoneConfig.ps1') `
                -ConfigPath $configPath `
                -TargetPath $tempTarget
        } | Should -Throw '*WarmupScenarioPath not found*'
    }

    It 'rejects a missing harness-testing fixture manifest when fixture mode is enabled' {
        $tempTarget = Join-Path -Path $TestDrive -ChildPath 'missing-fixture-manifest'
        Copy-Item -Path $fixtureDir -Destination $tempTarget -Recurse
        $cfgPath = Join-Path -Path $tempTarget -ChildPath '.hone' -AdditionalChildPath 'config.psd1'
        $content = Get-Content -Path $cfgPath -Raw
        $content = $content -replace "\r?\n\}\s*$", @"

    HarnessTesting = @{
        Enabled      = `$true
        ManifestPath = '.hone\fixtures\missing-fixture.psd1'
    }
}
"@
        Set-Content -Path $cfgPath -Value $content -Encoding utf8

        {
            & (Join-Path -Path $harnessRoot -ChildPath 'Test-HoneConfig.ps1') `
                -ConfigPath $configPath `
                -TargetPath $tempTarget
        } | Should -Throw '*HarnessTesting manifest not found*'
    }

    It 'rejects engine Fixer.MaxAttempts below 1' {
        $tempConfigPath = Join-Path -Path $TestDrive -ChildPath 'invalid-max-attempts.psd1'
        $content = Get-Content -Path $configPath -Raw
        $content = $content -replace 'MaxAttempts = 3', 'MaxAttempts = 0'
        Set-Content -Path $tempConfigPath -Value $content -Encoding utf8

        {
            & (Join-Path -Path $harnessRoot -ChildPath 'Test-HoneConfig.ps1') `
                -ConfigPath $tempConfigPath `
                -TargetPath $fixtureDir
        } | Should -Throw '*Fixer.MaxAttempts*'
    }

    It 'rejects engine Fixer.MaxDiffGrowthFactor below 1' {
        $tempConfigPath = Join-Path -Path $TestDrive -ChildPath 'invalid-diff-growth.psd1'
        $content = Get-Content -Path $configPath -Raw
        $content = $content -replace 'MaxDiffGrowthFactor = 3.0', 'MaxDiffGrowthFactor = 0.5'
        Set-Content -Path $tempConfigPath -Value $content -Encoding utf8

        {
            & (Join-Path -Path $harnessRoot -ChildPath 'Test-HoneConfig.ps1') `
                -ConfigPath $tempConfigPath `
                -TargetPath $fixtureDir
        } | Should -Throw '*Fixer.MaxDiffGrowthFactor*'
    }

    It 'rejects target Fixer.TestFileGuard values that are not boolean' {
        $tempTarget = Join-Path -Path $TestDrive -ChildPath 'invalid-target-fixer-guard'
        Copy-Item -Path $fixtureDir -Destination $tempTarget -Recurse
        $cfgPath = Join-Path -Path $tempTarget -ChildPath '.hone' -AdditionalChildPath 'config.psd1'
        $content = Get-Content -Path $cfgPath -Raw
        $content = $content -replace "\r?\n\}\s*$", @"

    Fixer = @{
        TestFileGuard = 'sometimes'
    }
}
"@
        Set-Content -Path $cfgPath -Value $content -Encoding utf8

        {
            & (Join-Path -Path $harnessRoot -ChildPath 'Test-HoneConfig.ps1') `
                -ConfigPath $configPath `
                -TargetPath $tempTarget
        } | Should -Throw '*Fixer.TestFileGuard*'
    }
}
