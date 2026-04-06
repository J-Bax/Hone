namespace Hone.SourceControl.Experiments;

/// <summary>
/// Options for reverting an experiment back to its original state.
/// </summary>
/// <param name="WorkingDir">Repository working directory.</param>
/// <param name="Experiment">Experiment number.</param>
/// <param name="BranchName">Expected experiment branch name.</param>
/// <param name="TargetFilePath">Absolute path to the file to restore.</param>
/// <param name="OriginalContent">Original file content to write back.</param>
/// <param name="Outcome">Human-readable outcome description for the revert commit message.</param>
public sealed record RevertExperimentOptions(
    string WorkingDir,
    int Experiment,
    string BranchName,
    string TargetFilePath,
    string OriginalContent,
    string Outcome);
