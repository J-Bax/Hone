@{
    Name = 'perfview-gc'
    Description = 'GC statistics via PerfView ETW (/GCOnly mode for minimal overhead)'
    Group = 'etw-gc'
    RequiresAdmin = $true
    OverheadImpact = 'low'
    DefaultSettings = @{
        MaxCollectSec = 150
        BufferSizeMB = 256
        StopTimeoutSec = 300
        ExportTimeoutSec = 300
    }
}
