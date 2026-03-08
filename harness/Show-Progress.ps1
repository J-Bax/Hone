<#
.SYNOPSIS
    Provides dynamic terminal progress indicators (spinners) for long-running operations.

.DESCRIPTION
    Contains Start-Spinner and Stop-Spinner functions that display an animated braille
    spinner with elapsed time using a background ThreadJob. The spinner overwrites the
    same terminal line via carriage return, keeping output clean.

    Uses [Console]::Write for thread-safe, stream-independent terminal updates that
    don't appear in log files or captured output.
#>

function Start-Spinner {
    <#
    .SYNOPSIS
        Starts an animated spinner on the current terminal line.
    .PARAMETER Message
        Text to display next to the spinner (e.g., "Analyzing performance data").
    .OUTPUTS
        A job object to pass to Stop-Spinner.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Message
    )

    # Skip spinner if output is redirected (non-interactive / piped)
    if ([Console]::IsOutputRedirected) {
        Write-Information "  … $Message" -InformationAction Continue
        return $null
    }

    $job = Start-ThreadJob -ScriptBlock {
        param($msg)
        $frames = @(
            [char]0x280B, [char]0x2819, [char]0x2839, [char]0x2838,
            [char]0x283C, [char]0x2834, [char]0x2826, [char]0x2827,
            [char]0x2807, [char]0x280F
        )
        $i = 0
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            $elapsed = [math]::Floor($sw.Elapsed.TotalSeconds)
            $frame = $frames[$i % $frames.Count]
            $line = "  $frame $msg... ${elapsed}s"
            # Pad to overwrite any previous longer line
            $padded = $line.PadRight(78)
            [Console]::Write("`r$padded")
            Start-Sleep -Milliseconds 120
            $i++
        }
    } -ArgumentList $Message

    return $job
}

function Stop-Spinner {
    <#
    .SYNOPSIS
        Stops a running spinner and optionally displays a completion message.
    .PARAMETER Job
        The job object returned by Start-Spinner.
    .PARAMETER CompletionMessage
        Optional message to display after clearing the spinner (prefixed with ✓).
    #>
    [CmdletBinding()]
    param(
        $Job,

        [string]$CompletionMessage
    )

    if ($null -eq $Job) {
        # Spinner was skipped (non-interactive) — just show completion
        if ($CompletionMessage) {
            Write-Information "  ✓ $CompletionMessage" -InformationAction Continue
        }
        return
    }

    Stop-Job $Job -ErrorAction SilentlyContinue
    Remove-Job $Job -Force -ErrorAction SilentlyContinue

    # Clear the spinner line
    [Console]::Write("`r$(' ' * 78)`r")

    if ($CompletionMessage) {
        Write-Information "  ✓ $CompletionMessage" -InformationAction Continue
    }
}
