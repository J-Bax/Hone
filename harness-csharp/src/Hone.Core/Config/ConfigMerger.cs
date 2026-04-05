using System.Reflection;

namespace Hone.Core.Config;

/// <summary>
/// Merges engine defaults, target overrides, and optional CLI overrides
/// into a single <see cref="HoneConfig"/>.
/// Replaces <c>Merge-HoneConfig</c> from HoneHelpers.psm1.
/// </summary>
public static class ConfigMerger
{
    /// <summary>
    /// Merges engine defaults + target overrides + optional CLI overrides.
    /// Mirrors <c>Merge-HoneConfig</c> semantics: section-level merge for objects,
    /// scalar override for primitives.
    /// </summary>
    /// <param name="engine">Base engine configuration with defaults.</param>
    /// <param name="target">Target-specific overrides (non-default values win).</param>
    /// <param name="cli">Optional CLI flag overrides (non-null values win over everything).</param>
    /// <returns>The merged configuration.</returns>
    public static HoneConfig Merge(HoneConfig engine, HoneConfig target, CliOverrides? cli = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(target);

        var merged = new HoneConfig(
            Api: MergeSection(engine.Api, target.Api),
            Tolerances: MergeTolerances(engine.Tolerances, target.Tolerances),
            ScaleTest: MergeSection(engine.ScaleTest, target.ScaleTest),
            Loop: MergeSection(engine.Loop, target.Loop),
            Agents: MergeSection(engine.Agents, target.Agents),
            Diagnostics: MergeSection(engine.Diagnostics, target.Diagnostics),
            Logging: MergeSection(engine.Logging, target.Logging),
            Implementer: MergeSection(engine.Implementer, target.Implementer),
            DotnetCounters: MergeSection(engine.DotnetCounters, target.DotnetCounters));

        return cli is not null ? ApplyCliOverrides(merged, cli) : merged;
    }

    /// <summary>
    /// Merges two section records by comparing target property values against
    /// a default-constructed instance. Properties that differ from default
    /// (i.e., were explicitly set in the target) override the engine value.
    /// </summary>
    /// <remarks>
    /// For reference-type collection properties (e.g. <c>IReadOnlyList&lt;string&gt;</c>),
    /// the comparison uses <see cref="object.Equals(object?, object?)"/> which is reference equality.
    /// This means collection properties from the target section always win, even if they carry
    /// default content. This is an acceptable Phase 1 limitation since target configs should
    /// only specify sections they intend to override.
    /// </remarks>
    internal static T MergeSection<T>(T engine, T target)
        where T : notnull
    {
        // Record types have exactly one public constructor (the primary constructor)
        ConstructorInfo ctor = typeof(T).GetConstructors()[0];
        ParameterInfo[] parameters = ctor.GetParameters();
        object?[] defaultArgs = BuildDefaultArgs(parameters);
        object defaults = ctor.Invoke(defaultArgs)!;

        object?[] mergedArgs = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            PropertyInfo? prop = typeof(T).GetProperty(
                parameters[i].Name!,
                BindingFlags.Public | BindingFlags.Instance);

            if (prop is null)
            {
                mergedArgs[i] = defaultArgs[i];
                continue;
            }

            object? engineVal = prop.GetValue(engine);
            object? targetVal = prop.GetValue(target);
            object? defaultVal = prop.GetValue(defaults);

            mergedArgs[i] = Equals(targetVal, defaultVal) ? engineVal : targetVal;
        }

        return (T)ctor.Invoke(mergedArgs);
    }

    /// <summary>
    /// Special handling for TolerancesConfig: merges the nested Efficiency sub-section
    /// recursively before merging the parent section.
    /// </summary>
    private static TolerancesConfig MergeTolerances(TolerancesConfig engine, TolerancesConfig target)
    {
        TolerancesConfig merged = MergeSection(engine, target);

        // MergeSection compares Efficiency as a record (value equality), which works
        // correctly for EfficiencyConfig since it only has value-type properties.
        // If the target Efficiency differs from default, MergeSection already picked it.
        // But if target has a partially-overridden Efficiency, we need deeper merge.
        var defaultTolerances = new TolerancesConfig();
        if (!Equals(target.Efficiency, defaultTolerances.Efficiency))
        {
            EfficiencyConfig mergedEfficiency = MergeSection(engine.Efficiency, target.Efficiency);
            merged = merged with { Efficiency = mergedEfficiency };
        }

        return merged;
    }

    private static HoneConfig ApplyCliOverrides(HoneConfig config, CliOverrides cli)
    {
        return config with
        {
            Loop = config.Loop with
            {
                MaxExperiments = cli.MaxExperiments ?? config.Loop.MaxExperiments,
                StackedDiffs = cli.StackedDiffs ?? config.Loop.StackedDiffs,
                WaitForMerge = cli.WaitForMerge ?? config.Loop.WaitForMerge,
                SkipClassification = cli.SkipClassification ?? config.Loop.SkipClassification,
            },
            Agents = config.Agents with
            {
                DefaultModel = cli.Model ?? config.Agents.DefaultModel,
            },
            Diagnostics = config.Diagnostics with
            {
                Enabled = cli.DiagnosticsEnabled ?? config.Diagnostics.Enabled,
            },
        };
    }

    private static object?[] BuildDefaultArgs(ParameterInfo[] parameters)
    {
        object?[] args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
        }

        return args;
    }
}
