@{
    Name = 'cpu-hotspots'
    Description = 'CPU flamegraph hotspot analysis using PerfView stack data'
    RequiredCollectors = @('perfview-cpu')
    AgentName = 'hone-cpu-profiler'
    DefaultSettings = @{
        Model = 'claude-opus-4.6'
        MaxStacks = 100
    }
}
