namespace Hone.Reporting.Console;

/// <summary>
/// Renders a compatibility assessment report to the console.
/// Pure renderer — no file I/O.
/// </summary>
public static class AssessmentRenderer
{
    private const int BannerWidth = 50;

    /// <summary>
    /// Renders the assessment report to the provided writer.
    /// </summary>
    public static void Render(AssessmentViewModel model, IConsoleColorWriter writer)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(writer);

        RenderBanner(model, writer);
        RenderOverall(model, writer);
        RenderReadySection(model, writer);
        RenderWarningsSection(model, writer);
        RenderBlockersSection(model, writer);
        RenderOnboardingSummary(model, writer);
        RenderNextSteps(model, writer);
    }

    private static void RenderBanner(AssessmentViewModel model, IConsoleColorWriter writer)
    {
        string border = new('\u2550', BannerWidth);

        writer.WriteLine();
        writer.WriteLine(border, ConsoleColor.DarkCyan);
        writer.WriteLine($"  Hone Compatibility Assessment: {model.TargetName}", ConsoleColor.DarkCyan);
        writer.WriteLine(border, ConsoleColor.DarkCyan);
        writer.WriteLine();
    }

    private static void RenderOverall(AssessmentViewModel model, IConsoleColorWriter writer)
    {
        ConsoleColor color = ScoreColor(model.Score);
        string overall = model.Overall.ToUpperInvariant();
        writer.Write("  Overall: ");
        writer.Write(overall, color);
        writer.Write(" (");
        writer.Write($"{model.Score}/100", color);
        writer.WriteLine(")");
        writer.WriteLine();
    }

    private static void RenderReadySection(AssessmentViewModel model, IConsoleColorWriter writer)
    {
        if (model.ReadyItems.Count == 0)
        {
            return;
        }

        writer.WriteLine("  Ready", ConsoleColor.Green);
        foreach (AssessmentReadyViewModel item in model.ReadyItems)
        {
            writer.Write("    \u2705 ", ConsoleColor.Green);
            writer.Write(item.Area, ConsoleColor.Green);
            writer.WriteLine($" \u2014 {item.Detail}");
        }

        writer.WriteLine();
    }

    private static void RenderWarningsSection(AssessmentViewModel model, IConsoleColorWriter writer)
    {
        if (model.Warnings.Count == 0)
        {
            return;
        }

        writer.WriteLine("  Warnings", ConsoleColor.Yellow);
        foreach (AssessmentFindingViewModel finding in model.Warnings)
        {
            writer.Write("    \u26a0\ufe0f ", ConsoleColor.Yellow);
            writer.Write(finding.Area, ConsoleColor.Yellow);
            writer.WriteLine($" \u2014 {finding.Issue}");
            writer.WriteLine($"       Remediation: {finding.Remediation}");
        }

        writer.WriteLine();
    }

    private static void RenderBlockersSection(AssessmentViewModel model, IConsoleColorWriter writer)
    {
        if (model.Blockers.Count == 0)
        {
            return;
        }

        writer.WriteLine("  Blockers", ConsoleColor.Red);
        foreach (AssessmentFindingViewModel finding in model.Blockers)
        {
            writer.Write("    \U0001f534 ", ConsoleColor.Red);
            writer.Write(finding.Area, ConsoleColor.Red);
            writer.WriteLine($" \u2014 {finding.Issue}");
            writer.WriteLine($"       Remediation: {finding.Remediation}");
        }

        writer.WriteLine();
    }

    private static void RenderOnboardingSummary(AssessmentViewModel model, IConsoleColorWriter writer)
    {
        if (model.OnboardingSummary is null)
        {
            return;
        }

        writer.WriteLine("  Onboarding Summary", ConsoleColor.Cyan);
        writer.WriteLine($"    {model.OnboardingSummary}");
        writer.WriteLine();
    }

    private static void RenderNextSteps(AssessmentViewModel model, IConsoleColorWriter writer)
    {
        string overall = model.Overall.ToUpperInvariant();
        if (string.Equals(overall, "COMPATIBLE", StringComparison.Ordinal)
            || string.Equals(overall, "PARTIAL", StringComparison.Ordinal))
        {
            writer.WriteLine("  Next Steps", ConsoleColor.Cyan);
            writer.WriteLine("    Run `hone init` to generate the .hone/ configuration directory.");
            writer.WriteLine();
        }
    }

    private static ConsoleColor ScoreColor(int score) =>
        score switch
        {
            >= 75 => ConsoleColor.Green,
            >= 40 => ConsoleColor.Yellow,
            _ => ConsoleColor.Red,
        };
}
