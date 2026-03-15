# Plugin Contracts

This document specifies the interface contracts for Hone's diagnostic plugin framework. Collectors and analyzers must return objects with the shapes described below.

For architecture context, see [architecture.md](architecture.md).

---

## Collector Plugins

Collector plugins live in `harness/collectors/<name>/` and must contain three scripts.

### Start-Collector.ps1

**Purpose:** Starts the diagnostic data collection process.

**Required Parameters:**
| Parameter | Type | Description |
|-----------|------|-------------|
| `OutputDir` | string | Directory to write collection artifacts |
| `ApiProcess` | Process | The running API process (for PID-based attachment) |
| `Settings` | hashtable | Collector-specific settings from `config.psd1` |

**Required Return Shape:**
```powershell
@{
    Success = [bool]     # $true if collector started successfully
    Handle  = [object]   # Opaque handle passed to Stop-Collector (process, file handle, etc.)
}
```

### Stop-Collector.ps1

**Purpose:** Stops collection and finalizes raw artifacts.

**Required Parameters:**
| Parameter | Type | Description |
|-----------|------|-------------|
| `Handle` | object | The handle returned by Start-Collector |
| `Settings` | hashtable | Collector-specific settings from `config.psd1` |

**Required Return Shape:**
```powershell
@{
    Success       = [bool]       # $true if stopped cleanly
    ArtifactPaths = [string[]]   # Paths to raw artifacts (ETL files, CSVs, etc.)
}
```

### Export-CollectorData.ps1

**Purpose:** Transforms raw artifacts into analysis-ready formats.

**Required Parameters:**
| Parameter | Type | Description |
|-----------|------|-------------|
| `ArtifactPaths` | string[] | Paths from Stop-Collector |
| `OutputDir` | string | Directory for exported files |
| `Settings` | hashtable | Collector-specific settings from `config.psd1` |

**Required Return Shape:**
```powershell
@{
    Success       = [bool]       # $true if export succeeded
    ExportedPaths = [string[]]   # Paths to exported files (folded stacks, JSON reports, etc.)
    Summary       = [string]     # Brief human-readable summary of what was exported
}
```

---

## Analyzer Plugins

Analyzer plugins live in `harness/analyzers/<name>/` and must contain one script.

### Invoke-Analyzer.ps1

**Purpose:** Analyzes exported collector data using an AI agent and produces a structured report.

**Required Parameters:**
| Parameter | Type | Description |
|-----------|------|-------------|
| `ExportedPaths` | string[] | Paths from the collector's Export-CollectorData |
| `OutputDir` | string | Directory for analysis outputs |
| `Settings` | hashtable | Analyzer-specific settings from `config.psd1` |
| `ConfigPath` | string | Path to the harness config.psd1 |

**Required Return Shape:**
```powershell
@{
    Success      = [bool]     # $true if analysis completed
    Report       = [string]   # Full analysis report text
    Summary      = [string]   # Brief summary (1-2 sentences)
    PromptPath   = [string]   # Path to the saved analysis prompt
    ResponsePath = [string]   # Path to the saved agent response
}
```

---

## Configuration

Each plugin has a `.psd1` metadata file (e.g., `collector.psd1` or `analyzer.psd1`) that declares:

```powershell
@{
    Name        = 'perfview-cpu'
    Description = 'PerfView CPU sampling collector'
    Group       = 'cpu'           # Collectors with same Group run together
    Requires    = @('perfview')   # Required for analyzers: which collector(s) must run first
}
```

Collector settings in `config.psd1` are passed through the `Settings` parameter:

```powershell
# In config.psd1
Diagnostics = @{
    CollectorSettings = @{
        'perfview-cpu' = @{
            MaxCollectSec    = 150
            StopTimeoutSec   = 300
            ExportTimeoutSec = 300
        }
    }
}
```

---

## Adding a New Plugin

1. Create directory: `harness/collectors/<name>/` or `harness/analyzers/<name>/`
2. Add the required scripts following the contracts above
3. Add a `.psd1` metadata file
4. Add settings to `config.psd1` under `Diagnostics.CollectorSettings` or `Diagnostics.AnalyzerSettings`
5. The plugin framework auto-discovers plugins by scanning the configured paths
