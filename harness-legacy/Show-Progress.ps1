<#
.SYNOPSIS
    Provides dynamic terminal progress indicators (spinners) for long-running operations.

.DESCRIPTION
    Contains Start-Spinner and Stop-Spinner functions that display an animated braille
    spinner with elapsed time. Uses a System.Timers.Timer whose events fire on
    background ThreadPool threads — these die automatically when the process exits
    (e.g., via Ctrl+C), so no explicit cancellation handling is needed.

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
        A spinner object to pass to Stop-Spinner.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [string]$Message
    )

    # Skip spinner if output is redirected (non-interactive / piped)
    if ([Console]::IsOutputRedirected) {
        Write-Information "  … $Message" -InformationAction Continue
        return $null
    }

    $state = [hashtable]::Synchronized(@{
            Message = $Message
            Frames = @(
                [char]0x280B, [char]0x2819, [char]0x2839, [char]0x2838,
                [char]0x283C, [char]0x2834, [char]0x2826, [char]0x2827,
                [char]0x2807, [char]0x280F
            )
            Index = 0
            Stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        })

    if ($PSCmdlet.ShouldProcess('Spinner timer', 'Start')) {
        $timer = [System.Timers.Timer]::new(120)
        $timer.AutoReset = $true

        # The Elapsed event fires on a ThreadPool background thread.
        # Background threads are killed automatically on process exit (Ctrl+C).
        Register-ObjectEvent -InputObject $timer -EventName Elapsed -Action {
            $s = $Event.MessageData
            $elapsed = [math]::Floor($s.Stopwatch.Elapsed.TotalSeconds)
            $frame = $s.Frames[$s.Index % $s.Frames.Count]
            $line = "  $frame $($s.Message)... ${elapsed}s"
            try { $c = [System.Console]; $c::Write("`r$($line.PadRight(78))") } catch { Write-Verbose "Console write failed: $_" }
            $s.Index++
        } -MessageData $state | Out-Null

        $timer.Start()

        return @{ Timer = $timer; State = $state }
    }
}

function Stop-Spinner {
    <#
    .SYNOPSIS
        Stops a running spinner and optionally displays a completion message.
    .PARAMETER Spinner
        The spinner object returned by Start-Spinner.
    .PARAMETER CompletionMessage
        Optional message to display after clearing the spinner (prefixed with ✓).
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        $Spinner,

        [string]$CompletionMessage
    )

    if ($null -eq $Spinner) {
        # Spinner was skipped (non-interactive) — just show completion
        if ($CompletionMessage) {
            Write-Information "  ✓ $CompletionMessage" -InformationAction Continue
        }
        return
    }

    if ($PSCmdlet.ShouldProcess('Spinner timer', 'Stop')) {
        $Spinner.Timer.Stop()
        $Spinner.Timer.Dispose()

        # Unregister the event subscriber for this timer
        Get-EventSubscriber | Where-Object { $_.SourceObject -eq $Spinner.Timer } |
            ForEach-Object {
                Unregister-Event -SubscriptionId $_.SubscriptionId -ErrorAction SilentlyContinue
                Remove-Job -Id $_.Action.Id -Force -ErrorAction SilentlyContinue
            }

        # Clear the spinner line
        $c = [System.Console]; $c::Write("`r$(' ' * 78)`r")
    }

    if ($CompletionMessage) {
        Write-Information "  ✓ $CompletionMessage" -InformationAction Continue
    }
}
