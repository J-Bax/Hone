namespace Hone.Agents.Preparation;

/// <summary>
/// Options controlling the onboarding flow.
/// </summary>
public sealed record OnboardingOptions(
    string? Model = null,
    bool Force = false,
    bool DryRun = false);

/// <summary>
/// Aggregate result from the full onboarding pipeline.
/// </summary>
public sealed record OnboardingResult(
    bool Success,
    string Message,
    CompatibilityResult? Assessment = null,
    ScaffoldResult? Scaffold = null,
    ScaffoldWriteResult? WriteResult = null);

/// <summary>
/// Orchestrates the full onboarding flow: assess → scaffold → write.
/// </summary>
public sealed class OnboardingManager
{
    private const int MinScoreThreshold = 40;

    private readonly CompatibilityAgent _assessor;
    private readonly ScaffolderAgent _scaffolder;

    public OnboardingManager(CompatibilityAgent assessor, ScaffolderAgent scaffolder)
    {
        ArgumentNullException.ThrowIfNull(assessor);
        ArgumentNullException.ThrowIfNull(scaffolder);
        _assessor = assessor;
        _scaffolder = scaffolder;
    }

    /// <summary>
    /// Runs the full onboarding pipeline: assess compatibility, generate scaffold, write files.
    /// </summary>
    /// <param name="targetPath">Root directory of the target project.</param>
    /// <param name="options">Onboarding options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="OnboardingResult"/> with results from each stage.</returns>
    public async Task<OnboardingResult> OnboardAsync(
        string targetPath,
        OnboardingOptions options,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(options);

        // Step 1: Assess compatibility
        CompatibilityResult assessment = await _assessor
            .AssessAsync(targetPath, options.Model, ct).ConfigureAwait(false);

        if (!assessment.Success)
        {
            return new OnboardingResult(
                Success: false,
                Message: $"Assessment failed: {assessment.Message}",
                Assessment: assessment);
        }

        // Step 2: Check score threshold
        int score = assessment.Report?.Compatibility?.Score ?? 0;
        if (score < MinScoreThreshold && !options.Force)
        {
            return new OnboardingResult(
                Success: false,
                Message: $"Score too low ({score}/100). Use --force to proceed anyway.",
                Assessment: assessment);
        }

        // Step 3: Generate scaffold
        ScaffoldResult scaffold = await _scaffolder
            .ScaffoldAsync(assessment.Report!, assessment.PreProbe!, options.Model, ct)
            .ConfigureAwait(false);

        if (!scaffold.Success)
        {
            return new OnboardingResult(
                Success: false,
                Message: $"Scaffolding failed: {scaffold.Message}",
                Assessment: assessment,
                Scaffold: scaffold);
        }

        // Step 4: Dry run — return plan without writing
        if (options.DryRun)
        {
            return new OnboardingResult(
                Success: true,
                Message: "Dry run complete — no files written",
                Assessment: assessment,
                Scaffold: scaffold);
        }

        // Step 5: Write files
        ScaffoldWriteResult writeResult = await ScaffoldWriter
            .WriteAsync(targetPath, scaffold.Plan!, options.Force, ct)
            .ConfigureAwait(false);

        return new OnboardingResult(
            Success: true,
            Message: $"Onboarding complete: {writeResult.Written.Count} file(s) written, {writeResult.Skipped.Count} skipped",
            Assessment: assessment,
            Scaffold: scaffold,
            WriteResult: writeResult);
    }
}
