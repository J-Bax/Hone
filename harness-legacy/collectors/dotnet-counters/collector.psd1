@{
    Name = 'dotnet-counters'
    Description = '.NET runtime performance counters (CPU, GC, threads, allocations)'
    Group = 'default'
    RequiresAdmin = $false
    OverheadImpact = 'low'
    DefaultSettings = @{
        # Settings are inherited from the main DotnetCounters config section
    }
}
