# Plugin Contracts

This document specifies the C# interface contracts for Hone's diagnostic plugin framework. Collectors and analyzers implement the interfaces defined in `Hone.Core/Contracts/` and are auto-discovered at runtime by scanning the configured `CollectorsPath` and `AnalyzersPath` directories.

For architecture context, see [architecture.md](architecture.md).

---

## Collector Plugins

Collector plugins live in `harness-csharp/plugins/collectors/<name>/` and implement the `ICollectorPlugin` interface.

### `ICollectorPlugin` Interface

```csharp
/// <summary>
/// Defines a diagnostic data collector that attaches to the running API process
/// during a diagnostic measurement pass.
/// </summary>
public interface ICollectorPlugin
{
    /// <summary>Gets the unique collector name (matches the directory name).</summary>
    string Name { get; }

    /// <summary>Gets the collection group this collector belongs to.
    /// Collectors in the same group run together in one pass.
    /// Use "default" for lightweight, non-interfering collectors that run in every pass.
    /// </summary>
    string Group { get; }

    /// <summary>
    /// Starts data collection targeting the specified API process.
    /// </summary>
    /// <param name="outputDir">Directory to write collection artifacts.</param>
    /// <param name="processId">PID of the running API process (for attachment).</param>
    /// <param name="settings">Collector-specific settings from config.yaml.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="CollectorHandle"/> with Success flag and opaque handle.</returns>
    Task<CollectorHandle> StartAsync(
        string outputDir,
        int processId,
        IReadOnlyDictionary<string, object?> settings,
        CancellationToken ct = default);

    /// <summary>
    /// Stops collection and finalizes raw artifacts.
    /// </summary>
    /// <param name="handle">The handle returned by <see cref="StartAsync"/>.</param>
    /// <param name="settings">Collector-specific settings from config.yaml.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="CollectorArtifacts"/> with artifact paths.</returns>
    Task<CollectorArtifacts> StopAsync(
        CollectorHandle handle,
        IReadOnlyDictionary<string, object?> settings,
        CancellationToken ct = default);

    /// <summary>
    /// Transforms raw artifacts into analysis-ready formats.
    /// </summary>
    /// <param name="artifactPaths">Paths from <see cref="StopAsync"/>.</param>
    /// <param name="outputDir">Directory for exported files.</param>
    /// <param name="settings">Collector-specific settings from config.yaml.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="CollectorExport"/> with exported paths and summary.</returns>
    Task<CollectorExport> ExportAsync(
        IReadOnlyList<string> artifactPaths,
        string outputDir,
        IReadOnlyDictionary<string, object?> settings,
        CancellationToken ct = default);
}
```

### Return Types

```csharp
/// <summary>Result of ICollectorPlugin.StartAsync.</summary>
public sealed record CollectorHandle(
    bool Success,
    object? Handle   // Opaque handle passed to StopAsync (process, file handle, etc.)
);

/// <summary>Result of ICollectorPlugin.StopAsync.</summary>
public sealed record CollectorArtifacts(
    bool Success,
    IReadOnlyList<string> ArtifactPaths   // Paths to raw artifacts (ETL files, CSVs, etc.)
);

/// <summary>Result of ICollectorPlugin.ExportAsync.</summary>
public sealed record CollectorExport(
    bool Success,
    IReadOnlyList<string> ExportedPaths,  // Paths to exported files (folded stacks, JSON reports, etc.)
    string Summary                         // Brief human-readable summary of what was exported
);
```

**Required Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `outputDir` | string | Directory to write collection artifacts |
| `processId` | int | PID of the running API process (for attachment) |
| `settings` | `IReadOnlyDictionary<string, object?>` | Collector-specific settings from `config.yaml` |

---

## Analyzer Plugins

Analyzer plugins live in `harness-csharp/plugins/analyzers/<name>/` and implement the `IAnalyzerPlugin` interface.

### `IAnalyzerPlugin` Interface

```csharp
/// <summary>
/// Defines a diagnostic analyzer that consumes collector output and produces
/// an AI-generated analysis report.
/// </summary>
public interface IAnalyzerPlugin
{
    /// <summary>Gets the unique analyzer name (matches the directory name).</summary>
    string Name { get; }

    /// <summary>
    /// Gets the names of collectors whose output this analyzer requires.
    /// If a required collector's data is unavailable, this analyzer is skipped with a warning.
    /// </summary>
    IReadOnlyList<string> RequiredCollectors { get; }

    /// <summary>
    /// Gets the names of collectors whose output this analyzer can optionally use
    /// if available, but does not require.
    /// </summary>
    IReadOnlyList<string> OptionalCollectors { get; }

    /// <summary>
    /// Analyzes exported collector data and produces a structured analysis report.
    /// </summary>
    /// <param name="exportedPaths">Paths to exported files from collector(s).</param>
    /// <param name="outputDir">Directory for analysis outputs (prompt, response).</param>
    /// <param name="settings">Analyzer-specific settings from config.yaml.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="AnalyzerReport"/> with report text and metadata.</returns>
    Task<AnalyzerReport> AnalyzeAsync(
        IReadOnlyList<string> exportedPaths,
        string outputDir,
        IReadOnlyDictionary<string, object?> settings,
        CancellationToken ct = default);
}
```

### Return Type

```csharp
/// <summary>Result of IAnalyzerPlugin.AnalyzeAsync.</summary>
public sealed record AnalyzerReport(
    bool Success,
    string Report,        // Full analysis report text (markdown)
    string Summary,       // Brief summary (1-2 sentences)
    string PromptPath,    // Path to the saved analysis prompt
    string ResponsePath   // Path to the saved agent response
);
```

**Required Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `exportedPaths` | `IReadOnlyList<string>` | Paths from the collector's `ExportAsync` |
| `outputDir` | string | Directory for analysis outputs |
| `settings` | `IReadOnlyDictionary<string, object?>` | Analyzer-specific settings from `config.yaml` |

---

## Plugin Metadata

Each plugin provides a YAML metadata file (`collector.yaml` or `analyzer.yaml`) that the framework uses for discovery and dependency resolution:

```yaml
# harness-csharp/plugins/collectors/perfview-cpu/collector.yaml
Name: perfview-cpu
Description: PerfView CPU sampling collector
Group: etw-cpu           # Collectors with same Group run together in one pass
RequiresAdmin: true
OverheadImpact: medium
DefaultSettings:
  Enabled: true
  MaxCollectSec: 150
  StopTimeoutSec: 600
  ExportTimeoutSec: 600
  BufferSizeMB: 256
  MaxStacks: 100
```

```yaml
# harness-csharp/plugins/analyzers/cpu-hotspots/analyzer.yaml
Name: cpu-hotspots
Description: CPU hotspot analysis agent
RequiredCollectors:
  - perfview-cpu
OptionalCollectors: []
AgentName: hone-cpu-profiler
DefaultSettings:
  Enabled: true
  Model: claude-opus-4.6
  MaxStacks: 100
```

Plugin settings from `config.yaml` are merged with `DefaultSettings` and passed through the `settings` parameter.

---

## Adding a New Collector Plugin

1. **Create the plugin directory** with the required files:

   ```
   harness-csharp/plugins/collectors/<name>/
   ├── collector.yaml          # Metadata (Name, Group, RequiresAdmin, DefaultSettings)
   └── <Name>Collector.cs      # ICollectorPlugin implementation
   ```

2. **Implement `ICollectorPlugin`** following the interface contract above.

3. **Define the `Group`** field in `collector.yaml`:
   - Collectors in the **same group** run together in one pass
   - **Different groups** get separate passes (each with its own API instance + k6 run)
   - `Group: default` collectors run in **every** pass (use for lightweight, non-interfering tools)

4. **Add configuration** under `Diagnostics.CollectorSettings` in `harness-csharp/config.yaml` or `.hone/config.yaml`:

   ```yaml
   Diagnostics:
     CollectorSettings:
       thread-contention:
         Enabled: true
         MaxCollectSec: 90
   ```

5. The `DiagnosticCollectionOrchestrator` automatically discovers and runs all enabled collectors — no orchestrator changes needed.

---

## Adding a New Analyzer Plugin

1. **Create the plugin directory** with the required files:

   ```
   harness-csharp/plugins/analyzers/<name>/
   ├── analyzer.yaml       # Metadata (Name, RequiredCollectors, AgentName, DefaultSettings)
   ├── agent.md            # Copilot agent definition (also symlinked to .github/agents/)
   └── <Name>Analyzer.cs   # IAnalyzerPlugin implementation
   ```

2. **Implement `IAnalyzerPlugin`** following the interface contract above. Declare `RequiredCollectors` — if a required collector's data is unavailable (disabled, failed, or not yet run), the analyzer is **automatically skipped** with a warning.

3. **Write the agent definition** in `agent.md` with YAML frontmatter (`name`, `description`, `tools`) and the system prompt.

4. **Symlink the agent definition** into `.github/agents/`:

   ```sh
   # Windows: mklink /H .github/agents/hone-thread-profiler.agent.md harness-csharp/plugins/analyzers/thread-hotspots/agent.md
   ```

5. **Add configuration** under `Diagnostics.AnalyzerSettings` in `config.yaml`:

   ```yaml
   Diagnostics:
     AnalyzerSettings:
       thread-hotspots:
         Enabled: true
         Model: claude-opus-4.6
   ```

The `DiagnosticAnalysisOrchestrator` automatically discovers and runs all enabled analyzers. Reports are injected into the `hone-analyst` prompt under the "Diagnostic Profiling Reports" section.

