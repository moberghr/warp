using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// Answers one question about the measuring instrument rather than about Warp: does the allocation
/// column count bytes allocated on OTHER threads?
/// <para>
/// It matters because everything Warp does at speed happens on worker threads, not on the thread
/// BenchmarkDotNet invokes. If the column is thread-local, every allocation number this project
/// publishes is an underestimate and a separate diagnoser is needed; if it is process-wide, that
/// diagnoser is redundant. Both benchmarks allocate the same amount, differing only in which thread
/// does it, so the two rows read against each other are the answer.
/// </para>
/// </summary>
[Config(typeof(AllocationAttributionConfig))]
public class AllocationAttributionBenchmark
{
    private const int Chunks = 64;
    private const int ChunkBytes = 1024 * 1024;

    [Benchmark(Baseline = true)]
    public void AllocateOnBenchmarkThread()
    {
        Consume(Allocate());
    }

    [Benchmark]
    public void AllocateOnAnotherThread()
    {
        // Task.Run rather than a raw Thread: it is the shape Warp's workers actually use, so if the
        // pool threads were somehow attributed differently this would catch that too.
        Consume(Task.Run(Allocate).GetAwaiter().GetResult());
    }

    private static long Allocate()
    {
        long total = 0;

        for (var i = 0; i < Chunks; i++)
        {
            var buffer = new byte[ChunkBytes];

            // Touch it so nothing can optimise the allocation away.
            buffer[0] = (byte)i;
            total += buffer[0];
        }

        return total;
    }

    private static void Consume(long value)
    {
        if (value < 0)
        {
            throw new InvalidOperationException("unreachable, and here only so the result cannot be discarded");
        }
    }
}

/// <summary>Minimal config: this measures the harness, so it carries no fixture and no database.</summary>
public class AllocationAttributionConfig : ManualConfig
{
    public AllocationAttributionConfig()
    {
        AddDiagnoser(new MemoryDiagnoser(new MemoryDiagnoserConfig(false)));
        AddJob(Job.ShortRun.WithWarmupCount(1).WithIterationCount(3));
    }
}
