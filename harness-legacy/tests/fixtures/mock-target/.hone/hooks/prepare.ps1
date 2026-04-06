[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$TargetPath,
    [Parameter(Mandatory)] [hashtable]$Config,
    [string]$BaseUrl,
    [string]$Experiment
)

$null = $TargetPath, $Config, $BaseUrl, $Experiment

[PSCustomObject]@{
    Success = $true
    Message = 'Mock prepare completed'
    Duration = [timespan]::Zero
    Artifacts = @()
}
