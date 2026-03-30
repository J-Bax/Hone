<#
.SYNOPSIS
    Validates harness configuration and environment prerequisites.

.DESCRIPTION
    Checks config values are within valid ranges, required paths exist,
    and prerequisite tools are installed. Throws on the first fatal error.
    Warnings are emitted for non-fatal issues.

    When TargetPath is provided, also validates the target project's
    .hone/config.psd1 structure including hooks, scenario files, and
    required keys.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.

.PARAMETER TargetPath
    Root directory of the target project.  When provided, validates the
    .hone/config.psd1 structure inside the target.
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,

    [string]$TargetPath
)

Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

$config = Get-HoneConfig -ConfigPath $ConfigPath
$errors = @()
$warnings = @()
$harnessRoot = $PSScriptRoot

# ── Tolerance validation ────────────────────────────────────────────────────
if ($config.Tolerances) {
    $t = $config.Tolerances
    if ($null -ne $t.MaxRegressionPct -and ($t.MaxRegressionPct -lt 0 -or $t.MaxRegressionPct -gt 1)) {
        $errors += "Tolerances.MaxRegressionPct ($($t.MaxRegressionPct)) must be in [0, 1]"
    }
    if ($null -ne $t.MinImprovementPct -and ($t.MinImprovementPct -lt 0 -or $t.MinImprovementPct -gt 1)) {
        $errors += "Tolerances.MinImprovementPct ($($t.MinImprovementPct)) must be in [0, 1]"
    }
    if ($null -ne $t.MinAbsoluteP95DeltaMs -and $t.MinAbsoluteP95DeltaMs -lt 0) {
        $errors += "Tolerances.MinAbsoluteP95DeltaMs ($($t.MinAbsoluteP95DeltaMs)) must be >= 0"
    }
    if ($null -ne $t.StaleExperimentsBeforeStop -and $t.StaleExperimentsBeforeStop -lt 1) {
        $errors += "Tolerances.StaleExperimentsBeforeStop ($($t.StaleExperimentsBeforeStop)) must be >= 1"
    }
    if ($null -ne $t.MaxConsecutiveFailures -and $t.MaxConsecutiveFailures -lt 1) {
        $errors += "Tolerances.MaxConsecutiveFailures ($($t.MaxConsecutiveFailures)) must be >= 1"
    }
}

# ── Path validation ─────────────────────────────────────────────────────────
$repoRoot = Split-Path -Parent $PSScriptRoot
if ($config.Api) {
    if ($config.Api.SolutionPath) {
        $slnPath = Join-Path $repoRoot $config.Api.SolutionPath
        if (-not (Test-Path $slnPath)) {
            $errors += "Api.SolutionPath '$($config.Api.SolutionPath)' not found at $slnPath"
        }
    }
    if ($config.Api.ProjectPath) {
        $projPath = Join-Path $repoRoot $config.Api.ProjectPath
        if (-not (Test-Path $projPath)) {
            $errors += "Api.ProjectPath '$($config.Api.ProjectPath)' not found at $projPath"
        }
    }
}
if ($config.ScaleTest -and $config.ScaleTest.ScenarioPath) {
    $scenPath = Join-Path $repoRoot $config.ScaleTest.ScenarioPath
    if (-not (Test-Path $scenPath)) {
        $errors += "ScaleTest.ScenarioPath '$($config.ScaleTest.ScenarioPath)' not found at $scenPath"
    }
}

# ── Port validation ─────────────────────────────────────────────────────────
if ($config.Api -and $config.Api.BaseUrl) {
    try {
        $port = ([uri]$config.Api.BaseUrl).Port
        if ($port -ne 0 -and ($port -lt 1 -or $port -gt 65535)) {
            $errors += "Api.BaseUrl port ($port) must be 0 (dynamic) or in [1, 65535]"
        }
    } catch {
        $errors += "Api.BaseUrl '$($config.Api.BaseUrl)' is not a valid URL"
    }
}

# ── Numeric range validation ────────────────────────────────────────────────
if ($config.Api -and $null -ne $config.Api.StartupTimeout -and $config.Api.StartupTimeout -lt 1) {
    $errors += "Api.StartupTimeout ($($config.Api.StartupTimeout)) must be >= 1"
}
if ($config.ScaleTest -and $null -ne $config.ScaleTest.MeasuredRuns -and $config.ScaleTest.MeasuredRuns -lt 1) {
    $errors += "ScaleTest.MeasuredRuns ($($config.ScaleTest.MeasuredRuns)) must be >= 1"
}

# ── Tool availability ──────────────────────────────────────────────────────
foreach ($tool in @('dotnet', 'k6', 'git')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        $errors += "Required tool '$tool' is not installed or not on PATH"
    }
}
if (-not (Get-Command 'copilot' -ErrorAction SilentlyContinue)) {
    $warnings += "Copilot CLI ('copilot') not found on PATH — agent phases will fail"
}
if (-not (Get-Command 'gh' -ErrorAction SilentlyContinue)) {
    $warnings += "GitHub CLI ('gh') not found on PATH — PR creation will fail"
}

# ── Config interaction warnings ─────────────────────────────────────────────
if ($config.StackedDiffs -and $config.WaitForMerge) {
    $warnings += "StackedDiffs + WaitForMerge: works but defeats the purpose of stacked diffs"
}
if ($config.Diagnostics -and $config.Diagnostics.Enabled -and
    $config.Diagnostics.DiagnosticRuns -eq 0) {
    $warnings += "Diagnostics.Enabled=true but DiagnosticRuns=0: collectors start but no k6 data collected"
}
if ($config.ScaleTest -and $config.Tolerances -and
    $config.ScaleTest.MeasuredRuns -eq 1 -and $config.Tolerances.MaxRegressionPct -lt 0.05) {
    $warnings += "MeasuredRuns=1 with tight tolerance ($($config.Tolerances.MaxRegressionPct)): single-run noise may exceed tolerance"
}

# ── Target config validation (.hone/config.psd1) ───────────────────────────
if ($TargetPath) {
    $honeConfigPath = Join-Path -Path $TargetPath -ChildPath '.hone' 'config.psd1'
    if (-not (Test-Path $honeConfigPath)) {
        $errors += "Target config not found: $honeConfigPath"
    } else {
        $targetCfg = Import-PowerShellDataFile -Path $honeConfigPath

        # Required top-level keys
        foreach ($requiredKey in @('Name', 'BaseBranch', 'Api', 'Hooks', 'ScaleTest')) {
            if (-not $targetCfg.ContainsKey($requiredKey)) {
                $errors += ".hone/config.psd1 is missing required key: $requiredKey"
            }
        }

        if ($targetCfg.Api) {
            foreach ($requiredApiKey in @('SolutionPath', 'ProjectPath', 'TestProjectPath', 'ResultsPath', 'MetadataPath', 'BaseUrl', 'HealthEndpoint', 'StartupTimeout')) {
                if (-not $targetCfg.Api.ContainsKey($requiredApiKey)) {
                    $errors += ".hone/config.psd1 Api.$requiredApiKey is missing"
                }
            }

            foreach ($relativePathKey in @('SolutionPath', 'ProjectPath', 'TestProjectPath')) {
                if ($targetCfg.Api.ContainsKey($relativePathKey) -and $targetCfg.Api[$relativePathKey]) {
                    $resolvedPath = Join-Path -Path $TargetPath -ChildPath $targetCfg.Api[$relativePathKey]
                    if (-not (Test-Path $resolvedPath)) {
                        $errors += ".hone/config.psd1 Api.$relativePathKey not found: $resolvedPath"
                    }
                }
            }
        }

        # All 8 hooks must be declared
        if ($targetCfg.Hooks) {
            $requiredHooks = @('Prepare', 'Start', 'Stop', 'Ready', 'Warmup', 'Active', 'Cooldown', 'Cleanup')
            $validTypes = @('Script', 'Shared', 'Command', 'Http', 'Skip')
            foreach ($hookName in $requiredHooks) {
                if (-not $targetCfg.Hooks.ContainsKey($hookName)) {
                    $errors += ".hone/config.psd1 Hooks.$hookName is not declared"
                } else {
                    $hook = $targetCfg.Hooks[$hookName]
                    if (-not $hook.Type) {
                        $errors += ".hone/config.psd1 Hooks.$hookName is missing Type"
                    } elseif ($hook.Type -notin $validTypes) {
                        $errors += ".hone/config.psd1 Hooks.$hookName has invalid Type '$($hook.Type)' (valid: $($validTypes -join ', '))"
                    } elseif ($hook.Type -eq 'Script') {
                        $scriptPath = Join-Path -Path $TargetPath -ChildPath $hook.Path
                        if (-not (Test-Path $scriptPath)) {
                            $errors += ".hone/config.psd1 Hooks.$hookName script not found: $scriptPath"
                        }
                    } elseif ($hook.Type -eq 'Shared') {
                        if (-not $hook.Name) {
                            $errors += ".hone/config.psd1 Hooks.$hookName is missing Name for Shared hook"
                        } else {
                            $sharedHookPath = Join-Path -Path $harnessRoot -ChildPath 'hooks' -AdditionalChildPath "$($hook.Name).ps1"
                            if (-not (Test-Path $sharedHookPath)) {
                                $errors += ".hone/config.psd1 Hooks.$hookName shared hook not found: $sharedHookPath"
                            }
                        }
                    } elseif ($hook.Type -eq 'Http') {
                        if (-not $hook.Path) {
                            $errors += ".hone/config.psd1 Hooks.$hookName is missing Path for Http hook"
                        }
                    } elseif ($hook.Type -eq 'Command') {
                        if (-not $hook.Value) {
                            $errors += ".hone/config.psd1 Hooks.$hookName is missing Value for Command hook"
                        }
                    }
                }
            }
        }

        # Scenario files must exist
        if ($targetCfg.ScaleTest) {
            foreach ($requiredScaleKey in @('ScenarioPath', 'ScenarioRegistryPath', 'MeasuredRuns')) {
                if (-not $targetCfg.ScaleTest.ContainsKey($requiredScaleKey)) {
                    $errors += ".hone/config.psd1 ScaleTest.$requiredScaleKey is missing"
                }
            }
        }

        if ($targetCfg.ScaleTest -and $targetCfg.ScaleTest.ScenarioPath) {
            $scenFile = Join-Path -Path $TargetPath -ChildPath $targetCfg.ScaleTest.ScenarioPath
            if (-not (Test-Path $scenFile)) {
                $errors += ".hone/config.psd1 ScaleTest.ScenarioPath not found: $scenFile"
            }
        }

        $registryPath = $null
        if ($targetCfg.ScaleTest -and $targetCfg.ScaleTest.ScenarioRegistryPath) {
            $registryPath = Join-Path -Path $TargetPath -ChildPath $targetCfg.ScaleTest.ScenarioRegistryPath
            if (-not (Test-Path $registryPath)) {
                $errors += ".hone/config.psd1 ScaleTest.ScenarioRegistryPath not found: $registryPath"
            } else {
                try {
                    $registry = Get-Content -Path $registryPath -Raw | ConvertFrom-Json -ErrorAction Stop
                    $registryDir = Split-Path -Parent $registryPath
                    foreach ($scenarioName in $registry.scenarios.PSObject.Properties.Name) {
                        $scenarioEntry = $registry.scenarios.$scenarioName
                        if (-not $scenarioEntry.file) {
                            $errors += "Scenario registry entry '$scenarioName' is missing a file value"
                            continue
                        }
                        $registryScenarioPath = Join-Path -Path $registryDir -ChildPath $scenarioEntry.file
                        if (-not (Test-Path $registryScenarioPath)) {
                            $errors += "Scenario registry entry '$scenarioName' file not found: $registryScenarioPath"
                        }
                    }
                } catch {
                    $errors += ".hone/config.psd1 ScaleTest.ScenarioRegistryPath could not be parsed as JSON: $registryPath"
                }
            }
        }

        if ($targetCfg.ScaleTest -and $targetCfg.ScaleTest.WarmupEnabled) {
            if (-not $targetCfg.ScaleTest.WarmupScenarioPath) {
                $errors += ".hone/config.psd1 ScaleTest.WarmupScenarioPath is required when WarmupEnabled = `$true"
            } else {
                $warmupPath = Join-Path -Path $TargetPath -ChildPath $targetCfg.ScaleTest.WarmupScenarioPath
                if (-not (Test-Path $warmupPath)) {
                    $errors += ".hone/config.psd1 ScaleTest.WarmupScenarioPath not found: $warmupPath"
                }
            }
        }
    }
}

# ── Report results ──────────────────────────────────────────────────────────
foreach ($w in $warnings) {
    Write-Warning "Config: $w"
}

if ($errors.Count -gt 0) {
    $errorMsg = "Configuration validation failed:`n" + ($errors | ForEach-Object { "  - $_" } | Out-String)
    throw $errorMsg
}

Write-Verbose "Configuration validation passed"
