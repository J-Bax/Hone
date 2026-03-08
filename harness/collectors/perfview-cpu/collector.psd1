@{
    Name            = 'perfview-cpu'
    Description     = 'CPU sampling stacks via PerfView ETW kernel events'
    RequiresAdmin   = $true
    OverheadImpact  = 'moderate'
    DefaultSettings = @{
        MaxCollectSec = 90
        BufferSizeMB  = 256
    }
}
