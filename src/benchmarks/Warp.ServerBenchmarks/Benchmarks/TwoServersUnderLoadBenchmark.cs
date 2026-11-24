using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;
using Warp.Core.Handlers;
using Warp.ServerBenchmarks.Infrastructure;
using Warp.Worker;

namespace Warp.ServerBenchmarks.Benchmarks;

/// <summary>
/// Two servers draining one queue: what a cluster costs per job, against one server in the worker-modes
/// scenario.
/// <para>
/// Every other draining scenario runs one server, so none of them has two claims competing for the same rows,
/// a completion racing another server's renewal, or cluster-wide server tasks (rule 6.23) whose lease moves
/// between servers while signals arrive from both. The mix is chosen to reach all three: plain jobs for the
/// claim contention, jobs that run past the keep-alive interval (<c>CancellationCheckInterval</c> at 200 ms,
/// so they are renewed while the other server completes beside them), and batches with continuations, whose
/// completions signal the Orchestrator on whichever server finished them.
/// </para>
/// <para>
/// Both servers run DB push. Without it the server that did not publish learns of the work only from its own
/// polling backoff (10 s and up), and would sit out most of a drain this short, measuring one server with an
/// idle neighbour. With push the publisher's commit wakes the other server, as in a real cluster.
/// </para>
/// <para>
/// The scenario is only what it says while both servers take work, so each iteration counts the jobs each
/// server completed (from the completion log rows' worker ids) before its cleanup, and the report shows the
/// smaller server's share and fails the case when it is under 20%: a server that sat out the drain would
/// otherwise read as a cheap cluster.
/// </para>
/// </summary>
[CiScenario(
    "Two servers under load",
    "Two servers with 10 workers each, DB push on, drain one queue of 2,010 job rows: 1,400 empty jobs, 100 that run 300 ms, and five batches of 50 each followed by a continuation batch of 50 (500 jobs and 10 batch rows). Both worker modes.",
    "What a second server adds per job while it competes for the same rows: claim contention, completions beside the other server's renewals, and cluster-task leases moving between servers while both signal the Orchestrator. No other draining scenario runs more than one server.",
    Order = 15,
    Calibrated = false)]
[CaseLabel(nameof(UseDispatcher), "False", "single-worker")]
[CaseLabel(nameof(UseDispatcher), "True", "dispatcher")]
[CaseLabel(nameof(Provider), "PostgreSql", "PostgreSQL")]
[CaseLabel(nameof(Provider), "SqlServer", "SQL Server")]
[Config(typeof(ServerBenchmarkConfig))]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet manages lifecycle via [GlobalCleanup].")]
public class TwoServersUnderLoadBenchmark
{
    private const int Servers = 2;
    private const int SlowJobs = 100;
    private const int SlowJobMs = 300;
    private const int Batches = 5;
    private const int PerBatch = 50;

    private PostgresServerFixture _fixture = null!;

    [Params(2_000)]
    public int JobCount { get; set; }

    [Params(false, true)]
    public bool UseDispatcher { get; set; }

    [Params(BenchmarkProvider.PostgreSql, BenchmarkProvider.SqlServer)]
    public BenchmarkProvider Provider { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new PostgresServerFixture();
        await _fixture.InitializeAsync(
            workerCount: 10,
            useDispatcher: UseDispatcher,
            provider: Provider,
            serverCount: Servers,
            configure: config =>
            {
                config.UseDatabasePush();
                config.CancellationCheckInterval = TimeSpan.FromMilliseconds(200);
            });

        await PublishAsync(plainJobs: 100, slowJobs: 10, batches: 1);
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
        CountJobsPerServerAsync().GetAwaiter().GetResult();
        _fixture.CleanJobTables().GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task DrainOnTwoServers()
    {
        await PublishAsync(plainJobs: JobCount - SlowJobs - (Batches * PerBatch * 2), slowJobs: SlowJobs, batches: Batches);
        await _fixture.WaitForCompletion();
    }

    // The jobs each server completed this iteration, handed to the diagnoser (RoundTripCounter). A completion
    // log row carries the worker that ran the job, or in dispatcher mode possibly its group; both name a server.
    // Batch rows are completed by the Orchestrator with no worker and are left out. Harness work.
    private async Task CountJobsPerServerAsync()
    {
        var perServer = await _fixture.HarnessAsync(async context =>
        {
            var workers = await context.Set<Warp.Core.Data.Entities.Worker>()
                .TagWith(Harness.Tag)
                .AsNoTracking()
                .Select(x => new { x.Id, x.ServerId })
                .ToListAsync();
            var groups = await context.Set<WorkerGroup>()
                .TagWith(Harness.Tag)
                .AsNoTracking()
                .Select(x => new { x.Id, x.ServerId })
                .ToListAsync();
            var serverOf = workers
                .Concat(groups)
                .ToDictionary(x => x.Id, x => x.ServerId);

            var completedBy = await context.Set<JobLog>()
                .TagWith(Harness.Tag)
                .AsNoTracking()
                .Where(x => x.EventType == "Completed")
                .Where(x => x.WorkerId != null)
                .GroupBy(x => x.WorkerId)
                .Select(x =>
                    new
                    {
                        WorkerId = x.Key!.Value,
                        Count = x.Count(),
                    })
                .ToListAsync();

            return completedBy
                .Where(x => serverOf.ContainsKey(x.WorkerId))
                .GroupBy(x => serverOf[x.WorkerId])
                .ToDictionary(x => x.Key, x => (long)x.Sum(y => y.Count));
        });

        var servers = _fixture.ServerIds;
        RoundTripCounter.Add(RoundTripCounter.Slot.FirstServerJobs, perServer.GetValueOrDefault(servers[0]));
        RoundTripCounter.Add(RoundTripCounter.Slot.SecondServerJobs, perServer.GetValueOrDefault(servers[1]));
    }

    private async Task PublishAsync(int plainJobs, int slowJobs, int batches)
    {
        // The batches first, in their own save, so their children are claimed alongside the plain jobs rather
        // than after them, and the Orchestrator is signalled throughout the drain.
        var batchPublisher = _fixture.CreateBatchPublisher();
        for (var i = 0; i < batches; i++)
        {
            var parent = await batchPublisher.StartNew(Enumerable.Range(0, PerBatch).Select(_ => new EmptyRequest()).ToList());
            await batchPublisher.ContinueBatchWith(Enumerable.Range(0, PerBatch).Select(_ => new EmptyRequest()).ToList(), parent);
        }

        await batchPublisher.SaveChangesAsync();

        var publisher = _fixture.CreatePublisher();
        for (var i = 0; i < plainJobs; i++)
        {
            // The slow jobs spread through the plain ones, so both servers get some.
            if (slowJobs > 0 && i % (plainJobs / slowJobs) == 0)
            {
                await publisher.Enqueue(new DelayRequest { DelayMs = SlowJobMs });
            }

            await publisher.Enqueue(new EmptyRequest());
        }

        await publisher.SaveChangesAsync();
    }
}
