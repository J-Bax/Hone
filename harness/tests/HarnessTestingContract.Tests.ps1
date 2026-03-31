BeforeAll {
    $harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    Import-Module (Join-Path -Path $harnessRoot -ChildPath 'HoneHelpers.psm1') -Force
}

Describe 'Harness-testing contract data' {
    It 'exposes the canonical outcome vocabulary and artifact categories' {
        $contract = Get-HarnessTestingContract

        $contract.ExperimentOutcomes | Should -Contain 'improved'
        $contract.ExperimentOutcomes | Should -Contain 'build_failure'
        $contract.ExperimentOutcomes | Should -Contain 'analysis_failed'
        $contract.ArtifactCategories | Should -Contain 'analysis_prompt'
        $contract.ArtifactCategories | Should -Contain 'k6_summary'
        $contract.CoverageMap.Keys | Should -Contain 'acceptance-contract'
        $contract.CoverageMap.Keys | Should -Contain 'branch-contract'
        $contract.CoverageMap.Keys | Should -Contain 'determinism-contract'
        $contract.CoverageMap['branch-contract'] | Should -Contain 'Invoke-HoneLoop.Tests.ps1'
        $contract.CoverageMap['hook-contract'] | Should -Contain 'Get-PerformanceBaseline.Tests.ps1'
        $contract.CoverageMap['artifact-contract'] | Should -Contain 'Invoke-FailureHandler.Tests.ps1'
        $contract.CoverageMap['artifact-contract'] | Should -Contain 'Stage-ExperimentArtifacts.Tests.ps1'
        $contract.CoverageMap['determinism-contract'] | Should -Contain 'HarnessTestingTargetFixtures.Tests.ps1'
    }
}
