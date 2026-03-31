@{
    ExperimentOutcomes = @(
        'improved'
        'stale'
        'regressed'
        'build_failure'
        'test_failure'
        'prepare_failure'
        'api_start_failure'
        'scale_test_failure'
        'analysis_failed'
        'queue_init_failed'
        'fix_failed'
        'invalid_target'
        'apply_failed'
        'no_queue_items'
        'queued'
        'skipped'
    )

    ArtifactCategories = @(
        'analysis_prompt'
        'analysis_response'
        'classification_response'
        'fix_response'
        'root_cause'
        'build_output'
        'e2e_output'
        'e2e_trx'
        'k6_summary'
        'k6_log'
        'counter_metrics'
        'diagnostic_reports'
        'queue_state'
        'run_metadata'
        'hone_log'
        'baseline_metrics'
        'baseline_counter_metrics'
        'scenario_baselines'
        'root_cause_docs'
    )

    CoverageMap = @{
        'optimization-target-contract' = @(
            'Config-Validation.Tests.ps1'
            'Resolve-Hook.Tests.ps1'
            'Invoke-LifecycleHook.Tests.ps1'
            'HarnessTestingTargetFixtures.Tests.ps1'
        )
        'experiment-contract' = @(
            'Update-OptimizationMetadata.Tests.ps1'
            'Manage-OptimizationQueue.Tests.ps1'
        )
        'results-contract' = @(
            'Stage-ExperimentArtifacts.Tests.ps1'
            'Update-OptimizationMetadata.Tests.ps1'
        )
        'queue-contract' = @(
            'Manage-OptimizationQueue.Tests.ps1'
            'Build-AnalysisContext.Tests.ps1'
        )
        'branch-contract' = @(
            'Invoke-HoneLoop.Tests.ps1'
            'Build-PRBody.Tests.ps1'
            'HarnessTestingTargetFixtures.Tests.ps1'
        )
        'hook-contract' = @(
            'Resolve-Hook.Tests.ps1'
            'Invoke-LifecycleHook.Tests.ps1'
            'Config-Validation.Tests.ps1'
            'Get-PerformanceBaseline.Tests.ps1'
        )
        'acceptance-contract' = @(
            'Compare-Results.Tests.ps1'
        )
        'agent-wrapper-contract' = @(
            'Invoke-AnalysisAgent.Tests.ps1'
            'Invoke-ClassificationAgent.Tests.ps1'
            'Invoke-FixAgent.Tests.ps1'
            'Invoke-CopilotAgent.Tests.ps1'
        )
        'artifact-contract' = @(
            'Stage-ExperimentArtifacts.Tests.ps1'
            'Invoke-FailureHandler.Tests.ps1'
            'Invoke-HoneLoop.Tests.ps1'
            'Invoke-AnalysisAgent.Tests.ps1'
            'Invoke-E2ETests.Tests.ps1'
            'Invoke-Build.Tests.ps1'
            'HarnessTestingRuntime.Tests.ps1'
            'HarnessTestingTargetFixtures.Tests.ps1'
        )
        'determinism-contract' = @(
            'HarnessTestingFixture.Tests.ps1'
            'HarnessTestingRuntime.Tests.ps1'
            'HarnessTestingTargetFixtures.Tests.ps1'
        )
    }
}
