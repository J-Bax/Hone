<#
.SYNOPSIS
    Validates harness configuration and environment prerequisites.

.DESCRIPTION
    Checks config values are within valid ranges, required paths exist,
    and prerequisite tools are installed. Throws on the first fatal error.
    Warnings are emitted for non-fatal issues.

.PARAMETER ConfigPath
    Path to the harness config.psd1 file.
#>
[CmdletBinding()]
param(
    [string]$ConfigPath
)

Import-Module (Join-Path $PSScriptRoot 'HoneHelpers.psm1') -Force

$config = Get-HoneConfig -ConfigPath $ConfigPath
$errors = @()
$warnings = @()

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

# ── Report results ──────────────────────────────────────────────────────────
foreach ($w in $warnings) {
    Write-Warning "Config: $w"
}

if ($errors.Count -gt 0) {
    $errorMsg = "Configuration validation failed:`n" + ($errors | ForEach-Object { "  - $_" } | Out-String)
    throw $errorMsg
}

Write-Verbose "Configuration validation passed"
