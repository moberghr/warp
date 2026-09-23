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
        AddDiagnoser(new DatabaseStatementsDiagnoser());
        // BenchmarkDotNet's default build timeout is two minutes. That suits a microbenchmark, but
        // this project's benchmark assembly pulls in the whole server and its provider, and on a
        // loaded machine the generated build overruns it. The run is then reported as NA, which reads
        // as a broken benchmark rather than as a harness setting - the same way the missing provider
        // registration did.
        WithBuildTimeout(TimeSpan.FromMinutes(15));

        AddJob(Job.ShortRun
            .WithWarmupCount(1)
            .WithIterationCount(3));
    }
}
