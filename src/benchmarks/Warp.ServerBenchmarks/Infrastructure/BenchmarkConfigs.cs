using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;

namespace Warp.ServerBenchmarks.Infrastructure;

/// <summary>
/// BenchmarkDotNet config for component isolation benchmarks.
/// </summary>
public class ComponentBenchmarkConfig : ManualConfig
{
    public ComponentBenchmarkConfig()
    {
        AddDiagnoser(new MemoryDiagnoser(new MemoryDiagnoserConfig(false)));
        AddJob(Job.ShortRun);
    }
}

/// <summary>
/// BenchmarkDotNet config for full server benchmarks.
/// Adds TotalAllocatedDiagnoser alongside MemoryDiagnoser.
/// </summary>
public class ServerBenchmarkConfig : ManualConfig
{
    public ServerBenchmarkConfig()
    {
        // MemoryDiagnoser's Allocated is PROCESS-WIDE, not per benchmark thread. That is load-bearing
        // here, because everything Warp does at speed happens on worker threads rather than the one
        // BenchmarkDotNet invokes. AllocationAttributionBenchmark measures exactly this, and is the
        // reason no separate all-threads diagnoser exists any more.
        AddDiagnoser(new MemoryDiagnoser(new MemoryDiagnoserConfig(false)));
        AddDiagnoser(new PgStatStatementsDiagnoser());
        AddJob(Job.ShortRun
            .WithWarmupCount(1)
            .WithIterationCount(3));
    }
}
