BeforeAll {
    $harnessRoot = Join-Path -Path $PSScriptRoot -ChildPath '..'
    $scriptPath = Join-Path -Path $harnessRoot -ChildPath 'Build-PRBody.ps1'
    $null = $harnessRoot, $scriptPath
}

Describe 'Build-PRBody templates' {
    It 'builds accepted PR bodies with improvement details' {
        $body = & $scriptPath `
            -Type 'Accepted' `
            -Experiment 7 `
            -Description 'Optimize database projection' `
            -FilePath 'SampleApi/Controllers/ProductsController.cs' `
            -StackNote "> Stack note`n" `
            -DryRunNotice "> Dry run`n" `
            -MetricsSection "`n## Metrics`n" `
            -RcaSection "`n## RCA`n" `
            -ImprovementPct '18.4' `
            -ScenarioBreakdown "`n## Scenarios`n"

        $body | Should -Match '## Hone Experiment 7'
        $body | Should -Match '\*\*Optimization:\*\* Optimize database projection'
        $body | Should -Match '\*\*vs baseline improvement:\*\* 18\.4%'
        $body | Should -Match '## RCA'
        $body | Should -Match '## Metrics'
        $body | Should -Match '## Scenarios'
        $body | Should -Not -Match '\[REJECTED\]'
    }

    It 'builds rejected PR bodies with rejection context and artifact notice' {
        $body = & $scriptPath `
            -Type 'Rejected' `
            -Experiment 8 `
            -Description 'Optimize database projection' `
            -FilePath 'SampleApi/Controllers/ProductsController.cs' `
            -OutcomeLabel '**Regression**' `
            -OutcomeDetail 'p95 regressed by 12%' `
            -MetricsSection "`n## Metrics`n" `
            -RcaSection "`n## RCA`n"

        $body | Should -Match '## Hone Experiment 8 \[REJECTED\]'
        $body | Should -Match '\*\*Outcome:\*\* \*\*Regression\*\*'
        $body | Should -Match 'p95 regressed by 12%'
        $body | Should -Match 'This experiment was rejected\. The code change has been reverted\.'
        $body | Should -Match 'This PR contains only the experiment artifacts for the record\.'
    }
}
