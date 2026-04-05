@{
    Name = 'perfview-cpu'
    Description = 'CPU sampling stacks via PerfView ETW (CLR events + allocation sampling)'
    Group = 'etw-cpu'
    RequiresAdmin = $true
    OverheadImpact = 'moderate'
    DefaultSettings = @{
        MaxCollectSec = 150
        BufferSizeMB = 256
        StopTimeoutSec = 300
        ExportTimeoutSec = 300
        MaxStacks = 100
    }
}
