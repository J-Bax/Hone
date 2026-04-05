BeforeAll {
    Import-Module (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath 'HoneHelpers.psm1') -Force
    $fixtureDir = Join-Path -Path $PSScriptRoot -ChildPath 'fixtures' -AdditionalChildPath 'mock-target'
    $harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $targetConfig = Import-PowerShellDataFile (Join-Path -Path $fixtureDir -ChildPath '.hone' -AdditionalChildPath 'config.psd1')
    $null = $harnessRoot, $targetConfig
}

Describe 'Resolve-Hook' {
    It 'resolves Script type to absolute path in target dir' {
        $result = Resolve-Hook -HookName 'Prepare' -TargetConfig $targetConfig -TargetDir $fixtureDir -HarnessRoot $harnessRoot
        $result.Type | Should -Be 'Script'
        $result.Path | Should -BeLike '*prepare.ps1'
        Test-Path $result.Path | Should -BeTrue
    }

    It 'resolves Shared type to harness hooks directory' {
        $result = Resolve-Hook -HookName 'Start' -TargetConfig $targetConfig -TargetDir $fixtureDir -HarnessRoot $harnessRoot
        $result.Type | Should -Be 'Script'
        $result.Path | Should -BeLike '*hooks*dotnet-start.ps1'
    }

    It 'returns Skip for Skip type' {
        $result = Resolve-Hook -HookName 'Ready' -TargetConfig $targetConfig -TargetDir $fixtureDir -HarnessRoot $harnessRoot
        $result.Type | Should -Be 'Skip'
    }

    It 'returns Http for Http type' {
        $result = Resolve-Hook -HookName 'Cooldown' -TargetConfig $targetConfig -TargetDir $fixtureDir -HarnessRoot $harnessRoot
        $result.Type | Should -Be 'Http'
        $result.Method | Should -Be 'POST'
        $result.Path | Should -Be '/diag/gc'
    }

    It 'throws on undeclared hook' {
        $badConfig = @{ Hooks = @{} }
        { Resolve-Hook -HookName 'Prepare' -TargetConfig $badConfig -TargetDir $fixtureDir -HarnessRoot $harnessRoot } | Should -Throw '*must declare*'
    }

    It 'throws on unknown hook type' {
        $badConfig = @{ Hooks = @{ Prepare = @{ Type = 'Invalid' } } }
        { Resolve-Hook -HookName 'Prepare' -TargetConfig $badConfig -TargetDir $fixtureDir -HarnessRoot $harnessRoot } | Should -Throw '*Unknown*'
    }

    It 'throws when Script path does not exist' {
        $badConfig = @{ Hooks = @{ Prepare = @{ Type = 'Script'; Path = '.hone\hooks\nonexistent.ps1' } } }
        { Resolve-Hook -HookName 'Prepare' -TargetConfig $badConfig -TargetDir $fixtureDir -HarnessRoot $harnessRoot } | Should -Throw '*not found*'
    }
}
