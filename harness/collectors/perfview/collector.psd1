@{
    Name            = 'perfview'
    Description     = 'Unified CPU sampling + GC events via single PerfView ETW session'
    RequiresAdmin   = $true
    OverheadImpact  = 'moderate'
    DefaultSettings = @{
        MaxCollectSec      = 90
        BufferSizeMB       = 256
        AllocationSampling = $true
        StopTimeoutSec     = 300
    }
}
