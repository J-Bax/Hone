namespace Hone.Core.Contracts;

/// <summary>
/// Options for creating a pull request on a code host.
/// </summary>
public sealed record CreatePrOptions(
    string BaseBranch,
    string HeadBranch,
    string Title,
    string Body,
    string? WorkingDirectory = null);
