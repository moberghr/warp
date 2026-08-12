using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Concurrency;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Core.Retry;
using Warp.Core.Timeout;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Observability;

/// <summary>
/// RSC11 — the cross-key claim. One mixed workload drives every outcome class Warp can produce, then the
/// COMPLETE <c>stats:</c> surface it left behind is asserted key by key against an exact expected map.
/// <para>
/// Every individual behaviour here is already covered elsewhere (<see cref="OutcomeMetricsTestsBase"/> for
/// the reason breakdown, <see cref="RequeueStatsTestsBase"/> for the manual / recovery requeues,
/// <c>StatCounterTests</c> for append-only). What none of them can catch is a cross-key inconsistency that
/// each isolated test satisfies on its own: a state total that stops reconciling with its reasons once a
/// second reason exists, a key written twice from two paths, or a brand-new key nobody meant to add. The
/// assertion is therefore equality against a whole map, not a set of per-key <c>&gt;= 1</c> probes.
/// </para>
/// <para>
/// <b>Why direct-drive workers rather than a booted <c>WarpTestServer</c>.</b> Exactness is the point, and a
/// live server cannot give it for the concurrency-Wait case: the requeue outcome is
/// <see cref="State.Enqueued"/> with <c>ScheduleTime = now</c>, so a polling worker re-picks the job and
/// requeues it again for as long as the slot stays held — a count that depends on wall-clock timing. Driving
/// <c>GetAndProcessJob</c> a fixed number of times makes every case's execution count a constant. The class
/// still joins the serialized <c>HeavyIntegration</c> collection (§4.7.1): it is not a server host, but it is
/// far and away the heaviest DB test in this namespace (~24 worker iterations plus a graceful-cancellation
/// wait inside one <c>[TimedFact]</c>), and thread-pool starvation from a neighbouring heavy host is exactly
/// what would push it past its budget.
/// </para>
/// <para>
/// <b>N = 2 per case (§0.4).</b> Every case drives two jobs, not one and not fifty. Two is what separates
/// "increments" from "increments once per job" while keeping the expected values literal constants.
/// </para>
/// </summary>
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class StatSurfaceTestsBase : IAsyncLifetime
{
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly Guid WorkerId = Guid.NewGuid();

    private readonly IDatabaseFixture _fixture;

    protected StatSurfaceTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();

        var ctx = _fixture.CreateContext();
        ctx.Set<Server>().Add(new Server
        {
            Id = ServerId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
            ServiceCount = 1,
        });
        ctx.Set<Warp.Core.Data.Entities.Worker>().Add(new Warp.Core.Data.Entities.Worker
        {
            Id = WorkerId,
            ServerId = ServerId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task MixedWorkload_WritesTheExactExpectedStatSurface()
    {
        // ── Act: nine outcome classes, two jobs each, each on its own queue so a requeued job can only be
        // re-picked by the calls its own case budgeted for.
        await DriveSuccesses();
        await DriveUnattributedFailures();
        await DriveCancellations();
        await DriveRetryThenSucceed();
        await DriveRetryThenExhaust();
        await DriveConcurrencySkip();
        await DriveConcurrencyWait();
        await DriveTimeoutDelete();
        await DriveManualRequeues();

        // ── Assert
        var counters = await ReadCounters();

        // The whole surface, lifetime keys. Equality against a map — a key nobody expected fails here just as
        // loudly as a wrong value, which is the property no per-key assertion elsewhere has.
        var expected = new SortedDictionary<string, int>(StringComparer.Ordinal)
        {
            ["stats:succeeded"] = 4,             // 2 plain + 2 recovered by retry
            ["stats:failed"] = 4,                // 2 with no retry policy + 2 exhausted
            ["stats:failed-retry-exhausted"] = 2,
            ["stats:deleted"] = 6,               // 2 cancelled + 2 concurrency-skipped + 2 timed out
            ["stats:deleted-concurrency"] = 2,
            ["stats:deleted-timeout"] = 2,
            ["stats:requeued"] = 10,             // 2 + 4 retry, 2 concurrency Wait, 2 manual
            ["stats:requeued-retry"] = 6,
            ["stats:requeued-concurrency"] = 2,
            ["stats:requeued-manual"] = 2,
            ["stats:retried-jobs"] = 4,          // 4 distinct jobs entered retry, out of 6 retry EVENTS
        };

        var lifetime = SumByBaseKey(counters, bucketed: false);
        Render(lifetime).ShouldBe(Render(expected));

        // Defect 1 in the spec: the removed decrement wrote the lifetime key and no hourly bucket, so a
        // lifetime total silently disagreed with the sum of its own buckets. Every key must now reconcile.
        var hourly = SumByBaseKey(counters, bucketed: true);
        Render(hourly).ShouldBe(Render(expected));

        // ── Invariants

        // The "not Completed" umbrella is derived, never stored (RSC6). Its value is the number of terminal
        // non-Completed outcomes this workload actually drove: 2 plain failures + 2 exhaustions + 2
        // cancellations + 2 concurrency skips + 2 timeouts.
        counters.ShouldNotContain(
            x => x.Key.StartsWith("stats:unsuccessful", StringComparison.Ordinal),
            "stats:unsuccessful is derived on read as failed + deleted, never written.");
        (lifetime["stats:failed"] + lifetime["stats:deleted"]).ShouldBe(10);

        // Each state total is the sum of its reason keys PLUS an unattributed remainder — the reasons are
        // written independently of the total, so a reader never has to sum them and an outcome no addon
        // claimed still lands in the total. Both directions matter: a missing reason key and a reason key
        // exceeding its parent are equally wrong.
        AttributedSum(lifetime, "stats:failed").ShouldBe(2);
        Remainder(lifetime, "stats:failed").ShouldBe(2, "the two failures with no retry policy carry no reason");

        AttributedSum(lifetime, "stats:deleted").ShouldBe(4);
        Remainder(lifetime, "stats:deleted").ShouldBe(2, "the two graceful cancellations carry no reason");

        AttributedSum(lifetime, "stats:requeued").ShouldBe(10);
        Remainder(lifetime, "stats:requeued").ShouldBe(0, "every requeue in this workload is attributable");

        AttributedSum(lifetime, "stats:succeeded").ShouldBe(0);
        Remainder(lifetime, "stats:succeeded").ShouldBe(4, "a completion carries no reason");

        // retried-jobs counts DISTINCT jobs that entered retry, not retry events. The two only diverge when a
        // job retries more than once, which is why the exhaustion case is given a budget of 2: four jobs
        // entered retry, producing six retry events.
        lifetime["stats:retried-jobs"].ShouldBe(4);
        lifetime["stats:retried-jobs"].ShouldBeLessThan(lifetime["stats:requeued-retry"]);

        // Append-only (RSC4) — across the whole Counter table, not just the stats: family.
        counters.ShouldNotContain(x => x.Value < 0, "every counter is append-only — no row may be negative.");
    }

    // Cases ────────────────────────────────────────────────────────────────────────────────────────────
    private async Task DriveSuccesses()
    {
        var queue = Queue("success");
        await SeedJobs(queue, typeof(UnitRequest), 2);

        await Run(CreateWorker(queue), 2);
    }

    private async Task DriveUnattributedFailures()
    {
        // No Retry addon on this worker at all — the handler throw reaches the worker with no JobOutcome, so
        // the failure is terminal and carries no reason. This is the case that makes the unattributed
        // remainder non-zero; with the addon registered even MaxRetries = 0 stamps RetryExhausted.
        var queue = Queue("fail");
        await SeedJobs(queue, typeof(ThrowExceptionRequest), 2);

        await Run(CreateWorker(queue), 2);
    }

    private async Task DriveCancellations()
    {
        var queue = Queue("cancel");
        var jobIds = await SeedJobs(queue, typeof(CancellableRequest), 2);
        var worker = CreateWorker(queue);

        foreach (var jobId in jobIds)
        {
            // Sequential, so each cancellation is observed on its own: start the iteration, wait until the
            // claim has committed, then request graceful cancellation through the real command service.
            var processing = worker.GetAndProcessJob(CancellationToken.None);

            await WarpTestServer.WaitUntil(
                async () => await ReadState(jobId) == State.Processing,
                TimeSpan.FromSeconds(3),
                Xunit.TestContext.Current.CancellationToken);

            await TestTasks.CreateJobCommandService(_fixture.CreateContext()).DeleteJob(jobId);

            (await processing).ShouldBeTrue();
            (await ReadState(jobId)).ShouldBe(State.Deleted);
        }
    }

    private async Task DriveRetryThenSucceed()
    {
        // Two jobs × (one throw, one success) = four iterations. Delays = [] puts the retry straight back in
        // Enqueued, so the same worker picks it up on its next call; whichever job each call happens to claim,
        // four calls settle exactly four executions.
        var queue = Queue("retry-ok");
        await SeedJobs(queue, typeof(FailFirstAttemptRequest), 2);

        await Run(CreateWorker(queue, maxRetries: 1), 4);
    }

    private async Task DriveRetryThenExhaust()
    {
        // Budget of 2 so each job retries TWICE before exhausting — the only shape that separates
        // stats:retried-jobs (distinct jobs) from stats:requeued-retry (events).
        var queue = Queue("retry-exhausted");
        await SeedJobs(queue, typeof(ThrowExceptionRequest), 2);

        await Run(CreateWorker(queue, maxRetries: 2), 6);
    }

    private async Task DriveConcurrencySkip()
    {
        var queue = Queue("mutex");
        var key = $"surface-mutex-{Guid.NewGuid():N}";
        await SeedJobs(queue, typeof(UnitRequest), 2, ConcurrencyMetadata(key));

        // Pre-holding the only slot is what makes this exact: the jobs are rejected on their single
        // iteration instead of racing a barrier-pinned sibling.
        var semaphores = new FakeSemaphoreProvider();
        await using var held = semaphores.HoldSlot($"warp:concurrency:{key}");

        await Run(CreateWorker(queue, maxRetries: 1, semaphores: semaphores), 2);
    }

    private async Task DriveConcurrencyWait()
    {
        var queue = Queue("semaphore");
        var key = $"surface-semaphore-{Guid.NewGuid():N}";
        await SeedJobs(queue, typeof(UnitRequest), 2, ConcurrencyMetadata(key, limit: 2, ConcurrencyMode.Wait));

        var semaphores = new FakeSemaphoreProvider();
        await using var held = semaphores.HoldSlot($"warp:concurrency:{key}", 2);

        await Run(CreateWorker(queue, maxRetries: 1, semaphores: semaphores), 2);
    }

    private async Task DriveTimeoutDelete()
    {
        // A zero-second budget cancels the handler's token immediately, so Delete mode is reached without
        // spending wall-clock time. CancellableRequest is the sanctioned Task.Delay handler (§4.5).
        var queue = Queue("timeout");
        await SeedJobs(queue, typeof(CancellableRequest), 2, TimeoutMetadata(seconds: 0));

        await Run(CreateWorker(queue, maxRetries: 1), 2);
    }

    private async Task DriveManualRequeues()
    {
        // Never polled by any worker above, so the requeued jobs stay Enqueued and cannot add executions.
        var queue = Queue("manual");
        var jobIds = await SeedJobs(queue, typeof(UnitRequest), 2, state: State.Failed);

        var svc = TestTasks.CreateJobCommandService(_fixture.CreateContext());
        foreach (var jobId in jobIds)
        {
            await svc.RequeueJob(jobId);
        }
    }

    // Helpers ──────────────────────────────────────────────────────────────────────────────────────────
    private static string Queue(string name) => $"stat-surface-{name}-{Guid.NewGuid():N}";

    private static string ConcurrencyMetadata(string key, int? limit = null, ConcurrencyMode? mode = null)
    {
        var dict = new Dictionary<string, object> { ["ConcurrencyKey"] = key };
        if (limit != null)
        {
            dict["ConcurrencyLimit"] = limit.Value;
        }

        if (mode != null)
        {
            dict["ConcurrencyMode"] = (int)mode.Value;
        }

        return JsonSerializer.Serialize(dict);
    }

    private static string TimeoutMetadata(int seconds) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["TimeoutSeconds"] = seconds });

    private static async Task Run(WarpWorkerService<TestContext> worker, int iterations)
    {
        for (var i = 0; i < iterations; i++)
        {
            var processed = await worker.GetAndProcessJob(CancellationToken.None);
            processed.ShouldBeTrue($"iteration {i + 1} of {iterations} found no job to process");
        }
    }

    private async Task<List<Guid>> SeedJobs(string queue, Type requestType, int count, string? metadata = null, State state = State.Enqueued)
    {
        var ids = new List<Guid>(count);
        var ctx = _fixture.CreateContext();
        for (var i = 0; i < count; i++)
        {
            var jobId = Guid.NewGuid();
            ids.Add(jobId);
            ctx.Set<Job>().Add(new Job
            {
                Id = jobId,
                Kind = JobKind.Job,
                CurrentState = state,
                Type = requestType.AssemblyQualifiedName,
                Message = "{}",
                CreateTime = DateTime.UtcNow,
                ScheduleTime = DateTime.UtcNow,
                Queue = queue,
                Metadata = metadata,
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        return ids;
    }

    private async Task<State> ReadState(Guid jobId) =>
        await _fixture.CreateContext()
            .Set<Job>()
            .AsNoTracking()
            .Where(x => x.Id == jobId)
            .Select(x => x.CurrentState)
            .FirstAsync(Xunit.TestContext.Current.CancellationToken);

    private async Task<List<Counter>> ReadCounters() =>
        await _fixture.CreateContext()
            .Set<Counter>()
            .AsNoTracking()
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

    /// <summary>
    /// Folds every <c>stats:</c> row onto its base key, taking either the lifetime rows (<c>stats:{name}</c>)
    /// or the bucketed ones (<c>stats:{name}:{yyyy-MM-dd-HH}</c>). Summing the buckets rather than matching a
    /// literal hour keeps the assertion free of wall-clock coupling across an hour boundary.
    /// </summary>
    private static SortedDictionary<string, int> SumByBaseKey(List<Counter> counters, bool bucketed)
    {
        var result = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var counter in counters)
        {
            if (!counter.Key.StartsWith("stats:", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = counter.Key.Split(':');
            var isBucketRow = parts.Length > 2;
            if (isBucketRow != bucketed)
            {
                continue;
            }

            var baseKey = $"{parts[0]}:{parts[1]}";
            result[baseKey] = result.GetValueOrDefault(baseKey) + counter.Value;
        }

        return result;
    }

    // Reason keys are "stats:{state}-{reason}"; the state total is "stats:{state}".
    private static int AttributedSum(SortedDictionary<string, int> map, string stateKey) =>
        map
            .Where(x => x.Key.StartsWith(stateKey + "-", StringComparison.Ordinal))
            .Sum(x => x.Value);

    private static int Remainder(SortedDictionary<string, int> map, string stateKey) =>
        map.GetValueOrDefault(stateKey) - AttributedSum(map, stateKey);

    // One key per line so a mismatch reports the whole surface, not "dictionaries differ".
    private static string Render(SortedDictionary<string, int> map) =>
        string.Join(Environment.NewLine, map.Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.Key} = {x.Value}")));

    private WarpWorkerService<TestContext> CreateWorker(string queue, int? maxRetries = null, FakeSemaphoreProvider? semaphores = null)
    {
        var queues = new[] { queue };
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddTestServerContext<TestContext>();
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());
        services.AddSingleton<IWarpSemaphoreProvider>(semaphores ?? new FakeSemaphoreProvider());
        services.TryAddSingleton(TimeProvider.System);

        // maxRetries == null is the "no addons at all" profile — the only way to observe a terminal failure
        // that carries no reason, since the Retry behaviour stamps RetryExhausted even on a zero budget.
        if (maxRetries != null)
        {
            var builder = new WarpBuilder<TestContext>(services);

            // Retry before Timeout (§2.12), and Concurrency before nothing here — no RateLimit in this workload.
            builder.AddRetry(o =>
            {
                o.MaxRetries = maxRetries.Value;

                // No backoff: the retry lands in Enqueued, so the next GetAndProcessJob call picks it up and
                // the case's execution count stays a constant instead of a wall-clock wait (§0.3 — the budget
                // is never the thing to change).
                o.Delays = [];
            });
            builder.AddConcurrency();
            builder.AddTimeout();
        }

        var workerConfig = new OptionsWrapper<WarpServerConfiguration>(new WarpServerConfiguration
        {
            WorkerCount = 1,
            ServerId = ServerId,
            Queues = queues,

            // Drives RunJobMonitor's tick — the graceful-cancellation case waits on it.
            CancellationCheckInterval = TimeSpan.FromMilliseconds(100),
            LogFlushInterval = TimeSpan.FromMilliseconds(100),
        });
        services.AddSingleton<IOptions<WarpServerConfiguration>>(workerConfig);
        services.AddSingleton<IOptions<WarpConfiguration>>(workerConfig);

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var groupConfig = new WorkerGroupConfiguration
        {
            WorkerCount = 1,
            Queues = queues,
        };

        return new WarpWorkerService<TestContext>(
            WorkerId,
            scopeFactory,
            new NullLogger<WarpWorkerService<TestContext>>(),
            workerConfig,
            groupConfig,
            TimeProvider.System,
            TestTasks.QueriesFromScope<TestContext>(scopeFactory),
            TestTasks.NullTransport,
            TestTasks.NullSignals);
    }
}
