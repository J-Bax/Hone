# =============================================================================
# PSScriptAnalyzer Settings for Hone Harness
# https://github.com/PowerShell/PSScriptAnalyzer
# =============================================================================
#
# Severity levels:
#   Error   — blocks commits via pre-commit hook
#   Warning — reported but does not block
#   Information — informational only
#
# To run manually:  ./Invoke-Lint.ps1
# To auto-fix:      ./Invoke-Lint.ps1 -Fix

@{
    # -------------------------------------------------------------------------
    # Severity: treat these as errors (block commits)
    # -------------------------------------------------------------------------
    Rules = @{
        # Avoid aliases like % (ForEach-Object), ? (Where-Object), etc.
        PSAvoidUsingCmdletAliases = @{
            Enable = $true
        }

        # Avoid positional parameters — use named parameters for clarity
        PSAvoidUsingPositionalParameters = @{
            Enable           = $true
            CommandAllowList = @('Join-Path')
        }

        # Ensure consistent brace/bracket placement
        PSPlaceOpenBrace = @{
            Enable             = $true
            OnSameLine         = $true
            NewLineAfter       = $true
            IgnoreOneLineBlock = $true
        }

        PSPlaceCloseBrace = @{
            Enable             = $true
            NewLineAfter       = $false
            IgnoreOneLineBlock = $true
            NoEmptyLineBefore  = $false
        }

        # Align with .editorconfig: 4-space indentation for PowerShell
        PSUseConsistentIndentation = @{
            Enable              = $true
            IndentationSize     = 4
            PipelineIndentation = 'IncreaseIndentationForFirstPipeline'
            Kind                = 'space'
        }

        # Align with .editorconfig: consistent whitespace
        PSUseConsistentWhitespace = @{
            Enable                          = $true
            CheckInnerBrace                 = $true
            CheckOpenBrace                  = $true
            CheckOpenParen                  = $true
            CheckOperator                   = $true
            CheckPipe                       = $true
            CheckPipeForRedundantWhitespace = $false
            CheckSeparator                  = $true
            CheckParameter                  = $false
        }

        # Align assignments for readability
        PSAlignAssignmentStatement = @{
            Enable         = $false
            CheckHashtable = $false
        }
    }

    # -------------------------------------------------------------------------
    # Excluded rules
    # -------------------------------------------------------------------------
    ExcludeRules = @(
        # 5 existing scripts use unapproved verbs (Apply-, Stage-, Undo-,
        # Sync-, Limit-). Renaming would break the harness orchestration.
        # TODO: Address in a follow-up task.
        'PSUseApprovedVerbs'

        # Show-Results.ps1 and Show-Progress.ps1 intentionally use Write-Host
        # for rich console UI output. This is acceptable for display scripts.
        'PSAvoidUsingWriteHost'

        # Many harness scripts modify system state (start processes, write files)
        # without ShouldProcess. Adding -WhatIf support is too invasive for now.
        'PSUseShouldProcessForStateChangingFunctions'

        # Allow using Invoke-Expression in controlled harness contexts
        'PSAvoidUsingInvokeExpression'

        # .editorconfig specifies utf-8 (no BOM). PSScriptAnalyzer wants BOM
        # for Unicode files, which conflicts with our convention.
        'PSUseBOMForUnicodeEncodedFile'

        # Many harness functions use plural nouns by convention (e.g.,
        # Get-EnabledCollectors, Invoke-AllScaleTests)
        'PSUseSingularNouns'
    )
}
