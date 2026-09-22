using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Warp.Core.Concurrency;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.ServerBenchmarks.Infrastructure;
using Warp.Test.Shared.Handlers;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// What the concurrency addon costs per job, across contention levels.
/// <para>
/// This is where the advisory-lock connection pool was found, so it is the arm most worth keeping
/// honest: the split was worth 84.3 statements per job down to 37.0 at eight keys, and nothing else
/// measured here would have shown it. Replaces the lab's <c>mutex-8</c> arm.
/// </para>
/// <para>
/// <c>Keys</c> is swept rather than fixed because the addon's cost is a function of contention, not a
/// constant: a key per job barely contends, one key serialises everything, and eight keys against
/// sixteen workers is the middle where surplus claims are rejected and requeued in bulk.
/// </para>
/// </summary>
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class ConcurrencyBenchmark
{
    private const int PublishBatchSize = 1000;
    private const int HandlerMs = 5;

    private PostgresServerFixture _fixture = null!;

    [Params(10_000)]
    public int JobCount { get; set; }

    [Params(8, 10_000)]
    public int Keys { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresServerFixture();
        await _fixture.InitializeAsync(workerCount: 16, useDispatcher: false, addConcurrency: true);

        await PublishAsync(100);
        await _fixture.WaitForCompletion();
        await _fixture.CleanJobTables();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _fixture.DisposeAsync();
    }

    [IterationCleanup]
    public void AfterIteration()
    {
        _fixture.CleanJobTables().GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task ProcessJobs()
    {
        await PublishAsync(JobCount);
        await _fixture.WaitForCompletion();
    }

    private async Task PublishAsync(int count)
    {
        var remaining = count;
        var index = 0;

        while (remaining > 0)
        {
            var publisher = _fixture.CreatePublisher();
            var batch = Math.Min(PublishBatchSize, remaining);

            for (var i = 0; i < batch; i++)
            {
                var parameters = new JobParameters();
                parameters.WithMutex($"g{KeyFor(index++)}", ConcurrencyMode.Wait);

                // DelayRequest, not EmptyRequest: a handler returning immediately holds its group for
                // microseconds, so the rejection rate collapses and the arm measures almost no
                // contention. The delay is what keeps a group genuinely busy while its siblings are
                // claimed and turned away.
                await publisher.Enqueue(new DelayRequest { DelayMs = HandlerMs }, parameters);
            }

            await publisher.SaveChangesAsync();
            remaining -= batch;
        }
    }

    /// <summary>
    /// Scatters a job index across the key set with a splitmix64 finalizer.
    /// <para>
    /// NOT <c>index % keys</c>. Round-robin assigns consecutive keys to consecutive rows, and the
    /// claim hands rows out in schedule order, so every worker receives a different key and the arm
    /// measures ZERO contention however many workers run — an artifact of publish order rather than a
    /// property of Warp. Being a pure function of the index it also stays reproducible across runs,
    /// which <c>Random.Shared</c> would not.
    /// </para>
    /// </summary>
    private ulong KeyFor(int index)
    {
        var mixed = ((ulong)index + 1) * 0x9E3779B97F4A7C15UL;
        mixed ^= mixed >> 30;
        mixed *= 0xBF58476D1CE4E5B9UL;
        mixed ^= mixed >> 27;

        return mixed % (ulong)Math.Max(Keys, 1);
    }
}
