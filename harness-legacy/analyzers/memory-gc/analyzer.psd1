@{
    Name = 'memory-gc'
    Description = 'Memory and GC pressure analysis using PerfView GC event data'
    RequiredCollectors = @('perfview-gc')
    OptionalCollectors = @('perfview-cpu')
    AgentName = 'hone-memory-profiler'
    DefaultSettings = @{
        Model = 'claude-opus-4.6'
    }
}
