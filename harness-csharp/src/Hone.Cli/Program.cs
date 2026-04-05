using System.CommandLine;
using Hone.Core.Config;
using Hone.Lifecycle.Validation;
using Hone.Orchestration.Loop;

namespace Hone.Cli;

/// <summary>
/// CLI entry point for the Hone optimization harness.
/// Builds a <see cref="RootCommand"/> tree with System.CommandLine
/// and dispatches to internal pipeline components.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        RootCommand rootCommand = new("Hone optimization harness")
        {
            BuildRunCommand(),
            BuildBaselineCommand(),
            BuildResultsCommand(),
            BuildDashboardCommand(),
            BuildValidateCommand(),
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

            IServiceProvider services = ServiceRegistration.Build(targetDir, config, dryRun);
            var loopRunner = (HoneLoopRunner)services.GetService(typeof(HoneLoopRunner))!;

            var options = new LoopOptions(
                TargetDir: targetDir,
                Config: config,
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

    // ── baseline ─────────────────────────────────────────────────────────

    private static Command BuildBaselineCommand()
    {
        Option<string> targetOption = CreateTargetOption();

        var command = new Command("baseline", "Establish performance baseline")
        {
            targetOption,
        };

        command.SetAction(static _ =>
        {
            // Baseline requires the full pipeline (load test runner, target app running).
            // This will be wired when the baseline orchestration is extracted from the loop.
            Console.Error.WriteLine("The 'baseline' command is not yet implemented.");
            Console.Error.WriteLine("Run 'hone run --target <path>' to establish a baseline as part of the loop.");
            return 1;
        });

        return command;
    }

    // ── results ──────────────────────────────────────────────────────────

    private static Command BuildResultsCommand()
    {
        Option<string> targetOption = CreateTargetOption();

        var command = new Command("results", "Show results in terminal")
        {
            targetOption,
        };

        command.SetAction(static _ =>
        {
            // ResultsRenderer.Render() requires a ResultsViewModel built from
            // experiment result files. This will be wired when result file loading
            // is extracted into a standalone reader.
            Console.Error.WriteLine("The 'results' command is not yet implemented.");
            Console.Error.WriteLine("Results are printed at the end of 'hone run'.");
            return 1;
        });

        return command;
    }

    // ── dashboard ────────────────────────────────────────────────────────

    private static Command BuildDashboardCommand()
    {
        Option<string> targetOption = CreateTargetOption();

        var command = new Command("dashboard", "Generate HTML dashboard")
        {
            targetOption,
        };

        command.SetAction(static _ =>
        {
            // DashboardExporter.Build() requires pre-serialised JSON data from
            // experiment result files. This will be wired when result file loading
            // is extracted into a standalone reader.
            Console.Error.WriteLine("The 'dashboard' command is not yet implemented.");
            return 1;
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
}
