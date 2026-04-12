using System.Text.Json;

namespace Hone.Agents.Preparation;

/// <summary>
/// Merges a <see cref="MigrationPlan"/> into a <see cref="ScaffoldPlan"/>,
/// allowing migration config to override scaffolded defaults.
/// </summary>
public static class ScaffoldPlanMerger
{
    private static readonly JsonSerializerOptions YamlStyleOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Produces a merged <see cref="ScaffoldPlan"/> where migration config
    /// replaces the scaffolder's <c>.hone/config.yaml</c> content.
    /// </summary>
    /// <param name="scaffold">The original scaffold plan from the scaffolder agent.</param>
    /// <param name="migration">The migration plan from the migrator agent.</param>
    /// <returns>A new <see cref="ScaffoldPlan"/> with merged content.</returns>
    public static ScaffoldPlan Merge(ScaffoldPlan scaffold, MigrationPlan migration)
    {
        ArgumentNullException.ThrowIfNull(scaffold);
        ArgumentNullException.ThrowIfNull(migration);

        if (scaffold.Files is null || scaffold.Files.Count == 0)
        {
            return scaffold;
        }

        if (migration.Config is null || migration.Config.Count == 0)
        {
            return scaffold;
        }

        // Build a mutable copy of the scaffold files
        var mergedFiles = new Dictionary<string, string>(scaffold.Files, StringComparer.Ordinal);

        // Replace .hone/config.yaml with migration config serialized as YAML-like JSON
        string configYaml = JsonSerializer.Serialize(migration.Config, YamlStyleOptions);
        mergedFiles[".hone/config.yaml"] = configYaml;

        // Append migration notes to scaffold notes
        string? mergedNotes = scaffold.Notes;
        if (migration.Notes is not null)
        {
            mergedNotes = mergedNotes is not null
                ? $"{mergedNotes}\n\nMigration: {migration.Notes}"
                : $"Migration: {migration.Notes}";
        }

        return new ScaffoldPlan
        {
            Files = mergedFiles,
            Notes = mergedNotes,
        };
    }
}
