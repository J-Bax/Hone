using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using Hone.Agents.CopilotCli;
using Hone.Agents.Core;
using Hone.Agents.Preparation;
using Hone.Core.Config;
using Hone.Core.Contracts;
using Hone.Core.Models;
using Hone.Lifecycle.Validation;
using Hone.Measurement.K6;
using Hone.Measurement.Orchestration;
using Hone.Orchestration.Loop;
using Hone.Reporting.Console;
using Hone.Reporting.Dashboard;

namespace Hone.Cli;

/// <summary>
/// CLI entry point for the Hone optimization harness.
/// Builds a <see cref="RootCommand"/> tree with System.CommandLine
/// and dispatches to internal pipeline components.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions AssessJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static async Task<int> Main(string[] args)
    {
        RootCommand rootCommand = new("Hone optimization harness")
        {
            BuildRunCommand(),
            BuildValidateCommand(),
            BuildBaselineCommand(),
            BuildResultsCommand(),
            BuildDashboardCommand(),
            BuildAssessCommand(),
            BuildInitCommand(),
        };

        ParseResult parseResult = rootCommand.Parse(args);
        return await parseResult.InvokeAsync(
            new InvocationConfiguration(), CancellationToken.None).ConfigureAwait(false);
    }

    // ── Shared option factories ──────────────────────────────────────────

    private static Option<string> CreateTargetOption() =>
        new("--target") { Description = "Path to the target project directory", Required = true };

    // ── run ──────────────────────────────────────────────────────────────

    private static Command BuildRunCommand()
    {
        Option<string> targetOption = CreateTargetOption();
        var maxExperimentsOption = new Option<int?>("--max-experiments")
        {
            Description = "Override max experiments from config",
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Skip slow operations and use synthetic metrics",
        };

        var command = new Command("run", "Run the optimization loop")
        {
            targetOption,
            maxExperimentsOption,
            dryRunOption,
        };

        command.SetAction(async (pr, ct) =>
        {
            string targetPath = pr.GetValue(targetOption)!;
            int? maxExperiments = pr.GetValue(maxExperimentsOption);
            bool dryRun = pr.GetValue(dryRunOption);

            (string targetDir, string configPath) = ResolveTarget(targetPath);

            var cliOverrides = new CliOverrides(MaxExperiments: maxExperiments);
            HoneConfig config = LoadAndMergeConfig(configPath, cliOverrides);

            IServiceProvider services = ServiceRegistration.Build(targetDir, config, configPath);
            var loopRunner = (HoneLoopRunner)services.GetService(typeof(HoneLoopRunner))!;

            var options = new LoopOptions(
                TargetDir: targetDir,
                Config: config,
                TargetName: config.Name ?? Path.GetFileName(targetDir),
                DefaultBranch: config.BaseBranch ?? "main",
                ResultsPath: config.Api.ResultsPath,
                DryRun: dryRun,
                MaxExperimentsOverride: maxExperiments);

            LoopResult result = await loopRunner.RunAsync(options, ct).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"Exit reason:     {result.ExitReason}");
            Console.WriteLine($"Experiments run: {result.ExperimentsRun}");
            Console.WriteLine($"Successes:       {result.SuccessCount}");
            Console.WriteLine($"Best P95:        {result.BestP95:F1}ms (experiment {result.BestExperiment})");
            Console.WriteLine($"Baseline P95:    {result.BaselineP95:F1}ms");

            return result.SuccessCount > 0 ? 0 : 1;
        });

        return command;
    }

    // ── validate ─────────────────────────────────────────────────────────

    private static Command BuildValidateCommand()
    {
        Option<string> targetOption = CreateTargetOption();

        var command = new Command("validate", "Validate configuration")
        {
            targetOption,
        };

        command.SetAction(pr =>
        {
            string targetPath = pr.GetValue(targetOption)!;

            (string targetDir, string configPath) = ResolveTarget(targetPath);
            HoneConfig config = LoadAndMergeConfig(configPath);

            ValidationResult result = ConfigValidator.ValidateEngineConfig(config, targetDir);

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine("Warnings:");
                foreach (string warning in result.Warnings)
                {
                    Console.WriteLine($"  \u26a0 {warning}");
                }
            }

            if (result.Errors.Count > 0)
            {
                Console.WriteLine("Errors:");
                foreach (string error in result.Errors)
                {
                    Console.WriteLine($"  \u2717 {error}");
                }
            }

            if (result.IsValid)
            {
                Console.WriteLine("Configuration is valid.");
            }

            return result.IsValid ? 0 : 1;
        });

        return command;
    }

    // ── baseline ─────────────────────────────────────────────────────────

    private static Command BuildBaselineCommand()
    {
        Option<string> targetOption = CreateTargetOption();
        var forceOption = new Option<bool>("--force")
        {
            Description = "Re-measure baseline even if one already exists",
        };

        var command = new Command("baseline", "Measure performance baseline")
        {
            targetOption,
            forceOption,
        };

        command.SetAction(async (pr, ct) =>
        {
            string targetPath = pr.GetValue(targetOption)!;
            bool force = pr.GetValue(forceOption);

            (string targetDir, string configPath) = ResolveTarget(targetPath);
            HoneConfig config = LoadAndMergeConfig(configPath);

            string resultsPath = Path.Combine(targetDir, config.Api.ResultsPath);
            string baselineDir = Path.Combine(resultsPath, "baseline");
            string baselineSummary = Path.Combine(baselineDir, "k6-summary.json");

            // Check for existing baseline
            if (File.Exists(baselineSummary) && !force)
            {
                MetricSet existing = await K6SummaryParser.ParseAsync(
                    baselineSummary, experiment: 0, run: 0, ct).ConfigureAwait(false);

                await Console.Out.WriteLineAsync("Baseline already exists:").ConfigureAwait(false);
                await Console.Out.WriteLineAsync($"  P95:  {existing.HttpReqDuration.P95:F1}ms").ConfigureAwait(false);
                await Console.Out.WriteLineAsync($"  Avg:  {existing.HttpReqDuration.Avg:F1}ms").ConfigureAwait(false);
                await Console.Out.WriteLineAsync($"  RPS:  {existing.HttpReqs.Rate:F0}").ConfigureAwait(false);
                await Console.Out.WriteLineAsync().ConfigureAwait(false);
                await Console.Out.WriteLineAsync("Use --force to re-measure.").ConfigureAwait(false);
                return 0;
            }

            Directory.CreateDirectory(baselineDir);

            IProcessRunner processRunner = new ProcessRunner();
            ILoadTestRunner loadTestRunner = new K6LoadTestRunner(processRunner);
            var baseUrl = new Uri(config.Api.BaseUrl);

            Console.WriteLine($"Running baseline measurement against {baseUrl}...");

            ScaleTestResult result = await ScaleTestOrchestrator.RunAsync(
                config.ScaleTest, loadTestRunner, baseUrl, baselineDir, experiment: 0, ct: ct)
                .ConfigureAwait(false);

            if (result.Metrics is null)
            {
                await Console.Error.WriteLineAsync("Baseline measurement failed — no metrics produced.").ConfigureAwait(false);
                await Console.Error.WriteLineAsync("Ensure the target API is running and reachable.").ConfigureAwait(false);
                return 1;
            }

            // Save run-metadata.json
            MachineInfo machineInfo = GatherMachineInfo();
            var metadata = new RunMetadata(
                TargetName: Path.GetFileName(targetDir),
                StartedAt: DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                MachineInfo: machineInfo,
                Experiments: []);

            string metadataPath = Path.Combine(resultsPath, "run-metadata.json");
            Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
            string metadataJson = JsonSerializer.Serialize(metadata, MetadataJsonOptions);
            await File.WriteAllTextAsync(metadataPath, metadataJson, ct).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("Baseline established:");
            Console.WriteLine($"  P95:      {result.Metrics.HttpReqDuration.P95:F1}ms");
            Console.WriteLine($"  Avg:      {result.Metrics.HttpReqDuration.Avg:F1}ms");
            Console.WriteLine($"  P99:      {result.Metrics.HttpReqDuration.P99:F1}ms");
            Console.WriteLine($"  RPS:      {result.Metrics.HttpReqs.Rate:F0}");
            Console.WriteLine($"  Requests: {result.Metrics.HttpReqs.Count}");
            Console.WriteLine($"  Errors:   {result.Metrics.HttpReqFailed.Rate:P1}");
            Console.WriteLine();
            Console.WriteLine($"Saved to {baselineDir}");
            return 0;
        });

        return command;
    }

    // ── results ──────────────────────────────────────────────────────────

    private static Command BuildResultsCommand()
    {
        Option<string> targetOption = CreateTargetOption();

        var command = new Command("results", "Show performance results in the terminal")
        {
            targetOption,
        };

        command.SetAction(async (pr, ct) =>
        {
            string targetPath = pr.GetValue(targetOption)!;

            (string targetDir, string configPath) = ResolveTarget(targetPath);
            HoneConfig config = LoadAndMergeConfig(configPath);

            string resultsPath = Path.Combine(targetDir, config.Api.ResultsPath);

            ResultsSnapshot snapshot = await ResultsDirectoryReader.LoadAsync(resultsPath, ct)
                .ConfigureAwait(false);

            var viewModel = new ResultsViewModel(
                Baseline: snapshot.Baseline,
                Experiments: snapshot.Experiments,
                Tolerances: config.Tolerances,
                Metadata: snapshot.Metadata,
                BaselineCounters: snapshot.BaselineCounters,
                Scenarios: snapshot.Scenarios);

            var writer = new SystemConsoleColorWriter();
            ResultsRenderer.Render(viewModel, writer);
            return 0;
        });

        return command;
    }

    // ── dashboard ────────────────────────────────────────────────────────

    private static Command BuildDashboardCommand()
    {
        Option<string> targetOption = CreateTargetOption();
        var outputOption = new Option<string?>("--output")
        {
            Description = "Output path for the HTML dashboard (default: <results>/dashboard.html)",
        };
        var openOption = new Option<bool>("--open")
        {
            Description = "Open the dashboard in the default browser",
        };

        var command = new Command("dashboard", "Generate an HTML performance dashboard")
        {
            targetOption,
            outputOption,
            openOption,
        };

        command.SetAction(async (pr, ct) =>
        {
            string targetPath = pr.GetValue(targetOption)!;
            string? outputPath = pr.GetValue(outputOption);
            bool open = pr.GetValue(openOption);

            (string targetDir, string configPath) = ResolveTarget(targetPath);
            HoneConfig config = LoadAndMergeConfig(configPath);

            string resultsPath = Path.Combine(targetDir, config.Api.ResultsPath);
            outputPath ??= Path.Combine(resultsPath, "dashboard.html");

            ResultsSnapshot snapshot = await ResultsDirectoryReader.LoadAsync(resultsPath, ct)
                .ConfigureAwait(false);

            DashboardData data = BuildDashboardData(snapshot, config);
            string html = DashboardExporter.Build(data);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllTextAsync(outputPath, html, ct).ConfigureAwait(false);

            Console.WriteLine($"Dashboard written to {Path.GetFullPath(outputPath)}");

            if (open)
            {
                OpenInBrowser(outputPath);
            }

            return 0;
        });

        return command;
    }

    // ── assess ─────────────────────────────────────────────────────────

    private static Command BuildAssessCommand()
    {
        Option<string> targetOption = CreateTargetOption();
        var modelOption = new Option<string?>("--model")
        {
            Description = "Override the AI model for assessment",
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Output raw JSON instead of formatted report",
        };

        var command = new Command("assess", "Assess target project compatibility with Hone")
        {
            targetOption,
            modelOption,
            jsonOption,
        };

        command.SetAction(async (pr, ct) =>
        {
            string targetPath = pr.GetValue(targetOption)!;
            string? model = pr.GetValue(modelOption);
            bool jsonOutput = pr.GetValue(jsonOption);

            string targetDir = Path.GetFullPath(targetPath);
            if (!Directory.Exists(targetDir))
            {
                await Console.Error.WriteLineAsync($"Target directory not found: {targetDir}").ConfigureAwait(false);
                return 2;
            }

            // Minimal config for agent invocation (no .hone/config.yaml needed)
            var config = new HoneConfig();
            IProcessRunner processRunner = new ProcessRunner();
            IAgentRunner agentRunner = new CopilotCliAgentRunner();
            var agentInvoker = new AgentInvoker(agentRunner, config.Agents);
            var compatibilityAgent = new CompatibilityAgent(agentInvoker, processRunner);

            CompatibilityResult result = await compatibilityAgent
                .AssessAsync(targetPath, model, ct).ConfigureAwait(false);

            if (!result.Success)
            {
                await Console.Error.WriteLineAsync(result.Message).ConfigureAwait(false);
                return 2;
            }

            if (jsonOutput)
            {
                string json = JsonSerializer.Serialize(result.Report, AssessJsonOptions);
                Console.WriteLine(json);
            }
            else
            {
                AssessmentViewModel viewModel = MapToViewModel(result.Report!);
                var writer = new SystemConsoleColorWriter();
                AssessmentRenderer.Render(viewModel, writer);
            }

            // Write assessment JSON file
            string outputPath = Path.Combine(targetDir, ".hone-assessment.json");
            string reportJson = JsonSerializer.Serialize(result.Report, AssessJsonOptions);
            await File.WriteAllTextAsync(outputPath, reportJson, ct).ConfigureAwait(false);
            Console.WriteLine();
            Console.WriteLine($"Full report written to: {outputPath}");

            string overall = result.Report?.Compatibility?.Overall ?? "unknown";
            return overall.Equals("incompatible", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        });

        return command;
    }

    // ── init ──────────────────────────────────────────────────────────────

    private static Command BuildInitCommand()
    {
        Option<string> targetOption = CreateTargetOption();
        var modelOption = new Option<string?>("--model")
        {
            Description = "Override the AI model for assessment and scaffolding",
        };
        var forceOption = new Option<bool>("--force")
        {
            Description = "Proceed even with low compatibility score and overwrite existing files",
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Show what would be generated without writing files",
        };

        var command = new Command("init", "Assess and scaffold .hone/ configuration for a target project")
        {
            targetOption,
            modelOption,
            forceOption,
            dryRunOption,
        };

        command.SetAction(async (pr, ct) =>
        {
            string targetPath = pr.GetValue(targetOption)!;
            string? model = pr.GetValue(modelOption);
            bool force = pr.GetValue(forceOption);
            bool dryRun = pr.GetValue(dryRunOption);

            string targetDir = Path.GetFullPath(targetPath);
            if (!Directory.Exists(targetDir))
            {
                await Console.Error.WriteLineAsync($"Target directory not found: {targetDir}").ConfigureAwait(false);
                return 2;
            }

            var config = new HoneConfig();
            IProcessRunner processRunner = new ProcessRunner();
            IAgentRunner agentRunner = new CopilotCliAgentRunner();
            var agentInvoker = new AgentInvoker(agentRunner, config.Agents);
            var compatibilityAgent = new CompatibilityAgent(agentInvoker, processRunner);
            var scaffolderAgent = new ScaffolderAgent(agentInvoker);
            var migratorAgent = new MigratorAgent(agentInvoker);
            var manager = new OnboardingManager(compatibilityAgent, scaffolderAgent, migratorAgent);

            var options = new OnboardingOptions(Model: model, Force: force, DryRun: dryRun);
            OnboardingResult result = await manager.OnboardAsync(targetPath, options, ct)
                .ConfigureAwait(false);

            // Render assessment if available
            if (result.Assessment?.Report is not null)
            {
                AssessmentViewModel viewModel = MapToViewModel(result.Assessment.Report);
                var writer = new SystemConsoleColorWriter();
                AssessmentRenderer.Render(viewModel, writer);
                Console.WriteLine();
            }

            if (!result.Success)
            {
                await Console.Error.WriteLineAsync(result.Message).ConfigureAwait(false);
                return result.Assessment is { Success: false } ? 1 : 2;
            }

            if (dryRun && result.Scaffold?.Plan?.Files is not null)
            {
                Console.WriteLine("Files that would be created:");
                foreach (string filePath in result.Scaffold.Plan.Files.Keys)
                {
                    Console.WriteLine($"  + {filePath}");
                }

                Console.WriteLine();
                Console.WriteLine(result.Message);
            }
            else if (result.WriteResult is not null)
            {
                if (result.WriteResult.Written.Count > 0)
                {
                    Console.WriteLine("Files written:");
                    foreach (string filePath in result.WriteResult.Written)
                    {
                        Console.WriteLine($"  + {filePath}");
                    }
                }

                if (result.WriteResult.Skipped.Count > 0)
                {
                    Console.WriteLine("Files skipped (already exist, use --force to overwrite):");
                    foreach (string filePath in result.WriteResult.Skipped)
                    {
                        Console.WriteLine($"  ~ {filePath}");
                    }
                }

                Console.WriteLine();
                Console.WriteLine(result.Message);
            }

            if (result.Scaffold?.Plan?.Notes is not null)
            {
                Console.WriteLine();
                Console.WriteLine($"Notes: {result.Scaffold.Plan.Notes}");
            }

            return 0;
        });

        return command;
    }

    private static AssessmentViewModel MapToViewModel(CompatibilityReport report)
    {
        CompatibilitySection compatibility = report.Compatibility ?? new CompatibilitySection();

        IReadOnlyList<AssessmentFindingViewModel> blockers = [.. (compatibility.Blockers ?? [])
            .Select(b => new AssessmentFindingViewModel(
                Area: b.Area ?? "unknown",
                Issue: b.Issue ?? "unknown",
                Remediation: b.Remediation ?? "N/A")),];

        IReadOnlyList<AssessmentFindingViewModel> warnings = [.. (compatibility.Warnings ?? [])
            .Select(w => new AssessmentFindingViewModel(
                Area: w.Area ?? "unknown",
                Issue: w.Issue ?? "unknown",
                Remediation: w.Remediation ?? "N/A")),];

        IReadOnlyList<AssessmentReadyViewModel> readyItems = [.. (compatibility.Ready ?? [])
            .Select(r => new AssessmentReadyViewModel(
                Area: r.Area ?? "unknown",
                Detail: r.Detail ?? "ready")),];

        return new AssessmentViewModel(
            TargetName: report.Target?.Name ?? "Unknown Target",
            Overall: compatibility.Overall ?? "unknown",
            Score: compatibility.Score ?? 0,
            Blockers: blockers,
            Warnings: warnings,
            ReadyItems: readyItems,
            OnboardingSummary: report.OnboardingPlan?.Summary);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the target directory and verifies <c>.hone/config.yaml</c> exists.
    /// </summary>
    private static (string TargetDir, string ConfigPath) ResolveTarget(string targetPath)
    {
        string targetDir = Path.GetFullPath(targetPath);
        string honeDir = Path.Combine(targetDir, ".hone");
        string configPath = Path.Combine(honeDir, "config.yaml");

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                $"Target directory '{targetDir}' does not contain .hone/config.yaml");
        }

        return (targetDir, configPath);
    }

    /// <summary>
    /// Loads the target configuration, merges with engine defaults,
    /// and optionally applies CLI overrides.
    /// </summary>
    private static HoneConfig LoadAndMergeConfig(string configPath, CliOverrides? cli = null)
    {
        var engine = new HoneConfig();
        HoneConfig target = ConfigLoader.Load(configPath);
        return ConfigMerger.Merge(engine, target, cli);
    }

    private static MachineInfo GatherMachineInfo()
    {
        string cpuName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown CPU";
        int cpuCores = Environment.ProcessorCount;
        decimal? totalRamGb = null;
        string osVersion = RuntimeInformation.OSDescription;
        string dotnetVersion = RuntimeInformation.FrameworkDescription;

        long totalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (totalMemory > 0)
        {
            totalRamGb = Math.Round((decimal)totalMemory / (1024 * 1024 * 1024), 1);
        }

        return new MachineInfo(cpuName, cpuCores, totalRamGb, osVersion, dotnetVersion);
    }

    /// <summary>
    /// Assembles <see cref="DashboardData"/> from loaded results.
    /// Serialises each payload to JSON matching the dashboard template placeholders.
    /// </summary>
    private static DashboardData BuildDashboardData(ResultsSnapshot snapshot, HoneConfig config)
    {
        // Per-experiment k6 data
        var dataEntries = new List<object>();
        foreach (ExperimentRow row in snapshot.Experiments)
        {
            MetricSet m = row.Metrics;
            dataEntries.Add(new
            {
                experiment = row.Experiment,
                label = $"Exp {row.Experiment}",
                p50 = m.HttpReqDuration.P50,
                p90 = m.HttpReqDuration.P90,
                p95 = m.HttpReqDuration.P95,
                avg = m.HttpReqDuration.Avg,
                max = m.HttpReqDuration.Max,
                rps = m.HttpReqs.Rate,
                reqCount = m.HttpReqs.Count,
                errRate = m.HttpReqFailed.Rate,
            });
        }

        // Per-experiment counter data
        var counterEntries = new List<object>();
        var counterChartEntries = new List<object>();
        foreach (ExperimentRow row in snapshot.Experiments)
        {
            counterEntries.Add(new
            {
                experiment = row.Experiment,
                cpuAvg = row.Counters?.CpuAvgPercent ?? 0,
                cpuMax = 0,
                heapMBAvg = 0,
                heapMBMax = 0,
                gen0 = 0,
                gen1 = 0,
                gen2 = 0,
                workingSetMB = row.Counters?.MemoryMB ?? 0,
                threadPoolMax = 0,
                exceptions = 0,
            });

            counterChartEntries.Add(new
            {
                experiment = row.Experiment,
                label = $"Exp {row.Experiment}",
                cpuAvg = row.Counters?.CpuAvgPercent ?? 0,
                cpuMax = 0,
                workingSetMB = row.Counters?.MemoryMB ?? 0,
                heapMBMax = 0,
            });
        }

        // Run metadata
        string runMetadataJson = snapshot.Metadata is not null
            ? JsonSerializer.Serialize(snapshot.Metadata, MetadataJsonOptions)
            : "null";

        // Per-scenario data
        var scenarioDict = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (ScenarioResult scenario in snapshot.Scenarios)
        {
            var entries = new List<object>();
            foreach (ExperimentRow row in scenario.Experiments)
            {
                MetricSet m = row.Metrics;
                entries.Add(new
                {
                    experiment = row.Experiment,
                    label = $"Exp {row.Experiment}",
                    p50 = m.HttpReqDuration.P50,
                    p90 = m.HttpReqDuration.P90,
                    p95 = m.HttpReqDuration.P95,
                    avg = m.HttpReqDuration.Avg,
                    max = m.HttpReqDuration.Max,
                    rps = m.HttpReqs.Rate,
                    reqCount = m.HttpReqs.Count,
                    errRate = m.HttpReqFailed.Rate,
                });
            }

            scenarioDict[scenario.ScenarioName] = entries;
        }

        return new DashboardData
        {
            DataJson = JsonSerializer.Serialize(dataEntries),
            CounterJson = JsonSerializer.Serialize(counterEntries),
            TimeSeriesJson = "{}",
            RunMetadataJson = runMetadataJson,
            ScenarioJson = JsonSerializer.Serialize(scenarioDict),
            CounterChartJson = JsonSerializer.Serialize(counterChartEntries),
            MinImprovePct = config.Tolerances.MinImprovementPct * 100,
            MaxRegressPct = config.Tolerances.MaxRegressionPct * 100,
        };
    }

    private static void OpenInBrowser(string path)
    {
        string fullPath = Path.GetFullPath(path);
        try
        {
            _ = Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Could not open browser: {ex.Message}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.Error.WriteLine($"Could not open browser: {ex.Message}");
        }
    }
}
