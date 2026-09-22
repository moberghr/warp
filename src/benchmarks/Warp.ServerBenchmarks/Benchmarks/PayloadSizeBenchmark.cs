using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Warp.Core.Handlers;
using Warp.ServerBenchmarks.Infrastructure;
using Warp.Test.Shared.Handlers;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// What a job's payload size costs, in dispatcher mode.
/// <para>
/// The zero-byte arm is the control, and it is the point of the pair rather than a filler row. The
/// 7.1.0 release claims the batched-completion column narrowing helps in proportion to payload size
/// and does nothing at all at zero length; without an arm that holds everything else constant and
/// varies only the payload, that claim rests on an assumption. With one, a change that shifts the
/// zero-byte arm is visibly not about payload.
/// </para>
/// <para>
/// Replaces the lab's <c>dispatcher-4kb</c> arm, which measured 9.9 statements per job.
/// </para>
/// </summary>
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class PayloadSizeBenchmark
{
    private const int PublishBatchSize = 1000;

    private PostgresServerFixture _fixture = null!;
    private string _payload = string.Empty;

    [Params(1_000, 10_000)]
    public int JobCount { get; set; }

    // 4 KB, and it is the ALLOCATIONS this size is chosen for, not the statement count. Payload does
    // not move statements per job at all (9.58 against 9.83 across a zero-byte control — noise), so an
    // arm justified on that basis would be dead weight. It moves allocations from 3.02 GB to 3.24 GB,
    // a 7.3% difference, which clears the 2% allocation gate with room to spare. That is what lets
    // this arm catch a change that starts writing the payload column needlessly — the regression class
    // #301 fixed, and one an empty-payload benchmark cannot see.
    //
    // The size itself is inherited from that measurement rather than derived from a real workload. The
    // requirement it has to meet is only that payload handling clears the gate, and 4 KB does; a much
    // smaller payload would not, and the arm would then be measuring nothing it could act on.
    //
    // The zero-byte control was measured and dropped from CI: it establishes the contrast once, and
    // paying for it on every pull request buys nothing the gate can use. Run it locally when
    // diagnosing whether a regression is payload-specific.
    [Params(4096)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _payload = RandomPayload(PayloadBytes);

        _fixture = new PostgresServerFixture();
        await _fixture.InitializeAsync(workerCount: 16, useDispatcher: true);

        // Warm the JIT, type caches, connection pool and dispatcher channel, so the first measured
        // iteration is not paying for all of them.
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

    /// <summary>
    /// Incompressible payload. A repeated character compresses to almost nothing, so TOAST and WAL
    /// costs vanish and a payload-width benchmark measures nothing — the trap the lab documents
    /// having fallen into once, and that the first version of THIS benchmark then repeated with
    /// <c>new string('x', bytes)</c>.
    /// </summary>
    private static string RandomPayload(int bytes)
    {
        const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        var chars = new char[bytes];
        for (var i = 0; i < bytes; i++)
        {
            chars[i] = Alphabet[Random.Shared.Next(Alphabet.Length)];
        }

        return new string(chars);
    }

    private async Task PublishAsync(int count)
    {
        var remaining = count;

        while (remaining > 0)
        {
            var publisher = _fixture.CreatePublisher();
            var batch = Math.Min(PublishBatchSize, remaining);

            for (var i = 0; i < batch; i++)
            {
                await publisher.Enqueue(new PayloadRequest1 { Data = _payload });
            }

            await publisher.SaveChangesAsync();
            remaining -= batch;
        }
    }
}
