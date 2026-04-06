namespace Hone.SourceControl.Experiments;

/// <summary>
/// Options for applying a suggestion to an experiment branch.
/// </summary>
/// <param name="WorkingDir">Repository working directory.</param>
/// <param name="BaseBranch">Branch to create the experiment from.</param>
/// <param name="Experiment">Experiment number.</param>
/// <param name="TargetFilePath">Absolute path to the file that receives the suggestion.</param>
/// <param name="SuggestionContent">Full replacement content for the target file.</param>
/// <param name="Description">Human-readable description for the commit message.</param>
/// <param name="BranchPrefix">Branch name prefix (e.g. <c>hone/experiment</c>).</param>
public sealed record ApplySuggestionOptions(
    string WorkingDir,
    string BaseBranch,
    int Experiment,
    string TargetFilePath,
    string SuggestionContent,
    string Description,
    string BranchPrefix = "hone/experiment");
