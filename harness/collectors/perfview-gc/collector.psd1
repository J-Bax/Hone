@{
    Name            = 'perfview-gc'
    Description     = 'GC events and allocation sampling via PerfView ETW'
    RequiresAdmin   = $true
    OverheadImpact  = 'low'
    DefaultSettings = @{
        AllocationSampling = $true
        MaxCollectSec      = 90
        BufferSizeMB       = 256
    }
}
