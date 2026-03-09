@{
    Name            = 'perfview-cpu'
    Description     = 'CPU sampling stacks via PerfView ETW (ThreadTime + CLR events)'
    Group           = 'etw-cpu'
    RequiresAdmin   = $true
    OverheadImpact  = 'moderate'
    DefaultSettings = @{
        MaxCollectSec    = 90
        BufferSizeMB     = 256
        StopTimeoutSec   = 300
        ExportTimeoutSec = 300
        MaxStacks        = 100
    }
}
