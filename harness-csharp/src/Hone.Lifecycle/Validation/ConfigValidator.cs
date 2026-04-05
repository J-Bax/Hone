using System.Collections.Frozen;
using Hone.Core.Config;
using Hone.Lifecycle.Hooks;

namespace Hone.Lifecycle.Validation;

/// <summary>
/// Validates harness and target configuration.
/// Replaces <c>Test-HoneConfig.ps1</c>.
/// </summary>
public static class ConfigValidator
{
    /// <summary>All 8 required lifecycle hooks.</summary>
    private static readonly FrozenSet<string> RequiredHooks =
        FrozenSet.ToFrozenSet(["Prepare", "Start", "Stop", "Ready", "Warmup", "Active", "Cooldown", "Cleanup"]);

    /// <summary>Valid hook types for target config validation.</summary>
    private static readonly FrozenSet<string> ValidHookTypes =
        FrozenSet.ToFrozenSet(["BuiltIn", "Command", "Http", "Skip"], StringComparer.OrdinalIgnoreCase);

    public static ValidationResult ValidateEngineConfig(HoneConfig config, string? rootPath = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        List<string> errors = [];
        List<string> warnings = [];

        ValidateTolerances(config.Tolerances, errors);
        ValidatePaths(config, rootPath, errors);
        ValidatePort(config.Api, errors);
        ValidateNumericRanges(config, errors);
        ValidateImplementerConfig(config.Implementer, "Engine config", errors);
        ValidateConfigInteractions(config, warnings);

        return new ValidationResult(
            IsValid: errors.Count == 0,
            Errors: errors,
            Warnings: warnings);
    }

    /// <summary>
    /// Validates the target project configuration.
    /// </summary>
#pragma warning disable IDE0060 // Reserved for future target path validation (file existence checks)
    public static ValidationResult ValidateTargetConfig(
        TargetConfig targetConfig, string targetPath)
#pragma warning restore IDE0060
    {
        ArgumentNullException.ThrowIfNull(targetConfig);

        List<string> errors = [];
        List<string> warnings = [];

        if (string.IsNullOrWhiteSpace(targetConfig.Name))
        {
            errors.Add(".hone/config.yaml is missing required key: Name");
        }

        ValidateHooks(targetConfig, errors);

        return new ValidationResult(
            IsValid: errors.Count == 0,
            Errors: errors,
            Warnings: warnings);
    }

    private static void ValidateTolerances(TolerancesConfig tolerances, List<string> errors)
    {
        if (tolerances.MaxRegressionPct is < 0 or > 1)
        {
            errors.Add($"Tolerances.MaxRegressionPct ({tolerances.MaxRegressionPct}) must be in [0, 1]");
        }

        if (tolerances.MinImprovementPct is < 0 or > 1)
        {
            errors.Add($"Tolerances.MinImprovementPct ({tolerances.MinImprovementPct}) must be in [0, 1]");
        }

        if (tolerances.MinAbsoluteP95DeltaMs < 0)
        {
            errors.Add($"Tolerances.MinAbsoluteP95DeltaMs ({tolerances.MinAbsoluteP95DeltaMs}) must be >= 0");
        }

        if (tolerances.StaleExperimentsBeforeStop < 1)
        {
            errors.Add($"Tolerances.StaleExperimentsBeforeStop ({tolerances.StaleExperimentsBeforeStop}) must be >= 1");
        }

        if (tolerances.MaxConsecutiveFailures < 1)
        {
            errors.Add($"Tolerances.MaxConsecutiveFailures ({tolerances.MaxConsecutiveFailures}) must be >= 1");
        }
    }

    private static void ValidatePaths(HoneConfig config, string? rootPath, List<string> errors)
    {
        if (rootPath is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(config.Api.SolutionPath))
        {
            string slnPath = Path.Combine(rootPath, config.Api.SolutionPath);
            if (!File.Exists(slnPath) && !Directory.Exists(slnPath))
            {
                errors.Add($"Api.SolutionPath '{config.Api.SolutionPath}' not found at {slnPath}");
            }
        }

        if (!string.IsNullOrEmpty(config.Api.ProjectPath))
        {
            string projPath = Path.Combine(rootPath, config.Api.ProjectPath);
            if (!Directory.Exists(projPath))
            {
                errors.Add($"Api.ProjectPath '{config.Api.ProjectPath}' not found at {projPath}");
            }
        }

        if (!string.IsNullOrEmpty(config.ScaleTest.ScenarioPath))
        {
            string scenPath = Path.Combine(rootPath, config.ScaleTest.ScenarioPath);
            if (!File.Exists(scenPath))
            {
                errors.Add($"ScaleTest.ScenarioPath '{config.ScaleTest.ScenarioPath}' not found at {scenPath}");
            }
        }
    }

    private static void ValidatePort(ApiConfig api, List<string> errors)
    {
        try
        {
            var uri = new Uri(api.BaseUrl);
            int port = uri.Port;
            if (port is not 0 and (< 1 or > 65535))
            {
                errors.Add($"Api.BaseUrl port ({port}) must be 0 (dynamic) or in [1, 65535]");
            }
        }
        catch (UriFormatException)
        {
            errors.Add($"Api.BaseUrl '{api.BaseUrl}' is not a valid URL");
        }
    }

    private static void ValidateNumericRanges(HoneConfig config, List<string> errors)
    {
        if (config.Api.StartupTimeout < 1)
        {
            errors.Add($"Api.StartupTimeout ({config.Api.StartupTimeout}) must be >= 1");
        }

        if (config.ScaleTest.MeasuredRuns < 1)
        {
            errors.Add($"ScaleTest.MeasuredRuns ({config.ScaleTest.MeasuredRuns}) must be >= 1");
        }
    }

    private static void ValidateImplementerConfig(
        ImplementerConfig config, string label, List<string> errors)
    {
        if (config.MaxAttempts < 1)
        {
            errors.Add($"{label} Implementer.MaxAttempts ({config.MaxAttempts}) must be >= 1");
        }

        if (config.MaxDiffGrowthFactor < 1)
        {
            errors.Add($"{label} Implementer.MaxDiffGrowthFactor ({config.MaxDiffGrowthFactor}) must be >= 1");
        }
    }

    private static void ValidateConfigInteractions(HoneConfig config, List<string> warnings)
    {
        if (config.Loop.StackedDiffs && config.Loop.WaitForMerge)
        {
            warnings.Add("StackedDiffs + WaitForMerge: works but defeats the purpose of stacked diffs");
        }

        if (config.Diagnostics.Enabled && config.Diagnostics.DiagnosticRuns == 0)
        {
            warnings.Add("Diagnostics.Enabled=true but DiagnosticRuns=0: collectors start but no k6 data collected");
        }

        if (config.ScaleTest.MeasuredRuns == 1 && config.Tolerances.MaxRegressionPct < 0.05)
        {
            warnings.Add($"MeasuredRuns=1 with tight tolerance ({config.Tolerances.MaxRegressionPct}): single-run noise may exceed tolerance");
        }
    }

    private static void ValidateHooks(TargetConfig targetConfig, List<string> errors)
    {
        foreach (string hookName in RequiredHooks)
        {
            if (!targetConfig.Hooks.TryGetValue(hookName, out TargetHookConfig? hook))
            {
                errors.Add($".hone/config.yaml Hooks.{hookName} is not declared");
                continue;
            }

            if (string.IsNullOrEmpty(hook.Type))
            {
                errors.Add($".hone/config.yaml Hooks.{hookName} is missing Type");
            }
            else if (!ValidHookTypes.Contains(hook.Type))
            {
                errors.Add($".hone/config.yaml Hooks.{hookName} has invalid Type '{hook.Type}' (valid: {string.Join(", ", ValidHookTypes)})");
            }
            else if (hook.Type.Equals("Http", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(hook.Path))
            {
                errors.Add($".hone/config.yaml Hooks.{hookName} is missing Path for Http hook");
            }
            else if (hook.Type.Equals("Command", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(hook.Value))
            {
                errors.Add($".hone/config.yaml Hooks.{hookName} is missing Value for Command hook");
            }
        }
    }
}
