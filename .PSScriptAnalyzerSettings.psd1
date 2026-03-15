# =============================================================================
# PSScriptAnalyzer Settings for Hone Harness
# https://github.com/PowerShell/PSScriptAnalyzer
# =============================================================================
#
# Policy: ALL rules enforced — no exclusions. Fix violations, don't suppress.
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
    # Configurable formatting and style rules
    # -------------------------------------------------------------------------
    Rules = @{
        # Avoid aliases like % (ForEach-Object), ? (Where-Object), etc.
        PSAvoidUsingCmdletAliases = @{
            Enable = $true
        }

        # Require named parameters for clarity — no exceptions
        PSAvoidUsingPositionalParameters = @{
            Enable = $true
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

        # 4-space indentation for PowerShell
        PSUseConsistentIndentation = @{
            Enable              = $true
            IndentationSize     = 4
            PipelineIndentation = 'IncreaseIndentationForFirstPipeline'
            Kind                = 'space'
        }

        # Consistent whitespace — all checks enabled
        PSUseConsistentWhitespace = @{
            Enable                          = $true
            CheckInnerBrace                 = $true
            CheckOpenBrace                  = $true
            CheckOpenParen                  = $true
            CheckOperator                   = $true
            CheckPipe                       = $true
            CheckPipeForRedundantWhitespace = $true
            CheckSeparator                  = $true
            CheckParameter                  = $true
        }

        # Alignment conflicts with PSUseConsistentWhitespace CheckOperator;
        # consistent operator spacing is the stricter, more important rule.
        PSAlignAssignmentStatement = @{
            Enable         = $false
            CheckHashtable = $false
        }

        # Enforce correct casing of cmdlet/parameter names
        PSUseCorrectCasing = @{
            Enable = $true
        }
    }

    # -------------------------------------------------------------------------
    # Excluded rules — DSC-only rules (not applicable to this project)
    # -------------------------------------------------------------------------
    ExcludeRules = @(
        'PSDSCDscExamplesPresent'
        'PSDSCDscTestsPresent'
        'PSDSCReturnCorrectTypesForDSCFunctions'
        'PSDSCStandardDSCFunctionsInResource'
        'PSDSCUseIdenticalMandatoryParametersForDSC'
        'PSDSCUseIdenticalParametersForDSC'
        'PSDSCUseVerboseMessageInDSCResource'
    )
}
