using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
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
        : this(Job.ShortRun.WithWarmupCount(1).WithIterationCount(3))
    {
    }

    protected ServerBenchmarkConfig(Job job)
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

        AddJob(job);
    }
}

/// <summary>
/// For a benchmark whose duration is set by a CLOCK rather than by the code under test: a rate limit of
/// 100 jobs per window drains 1,000 jobs in nine windows whatever Warp does. The default job's JIT pass,
/// warm-up and three iterations made such a benchmark wait out that clock five times per case, and the
/// perf workflow measures two commits: with 10 s windows RateLimitBenchmark alone took 41 of the
/// workflow's minutes, where every other scenario but one takes about five. What these benchmarks are run
/// for is the per-job database cost, which the diagnoser reads the same from one pass as from five.
/// <para>
/// So: no JIT pass and no pilot (the Monitoring strategy) and no warm-up (the benchmark warms or settles
/// itself in GlobalSetup). Three iterations all the same: comparing a commit with itself, statements per
/// job moved 3.4% on one iteration and 0.5% on three, and the gate sits at 5%.
/// </para>
/// </summary>
public class ClockBoundBenchmarkConfig : ServerBenchmarkConfig
{
    public ClockBoundBenchmarkConfig()
        : this(iterations: 3)
    {
    }

    protected ClockBoundBenchmarkConfig(int iterations)
        : base(Job.Default
            .WithStrategy(RunStrategy.Monitoring)
            .WithLaunchCount(1)
            .WithWarmupCount(0)
            .WithIterationCount(iterations)
            .WithInvocationCount(1)
            .WithUnrollFactor(1))
    {
    }
}

/// <summary>
/// One measured window and nothing else, for a benchmark whose single operation IS the observation
/// period (the idle server's 60 seconds). It has no spread to report, and the report shows a dash for
/// it; a second window would cost the whole period again to say how much the period varies.
/// </summary>
public class SingleWindowBenchmarkConfig : ClockBoundBenchmarkConfig
{
    public SingleWindowBenchmarkConfig()
        : base(iterations: 1)
    {
    }
}
