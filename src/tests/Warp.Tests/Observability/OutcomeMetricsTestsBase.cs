using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Core.Logging;
using Warp.Core.Retry;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Observability;

/// <summary>
/// The <c>stats:{state}-{reason}</c> breakdown and <c>stats:retried-jobs</c>, driven by a real worker
/// finalization rather than seeded <see cref="Counter"/> rows.
/// <para>
/// Written because deleting the whole reason / retried-jobs block from both worker paths left the suite
/// green: every other test in this namespace either seeds counters and reads them back, or asserts on
/// meters. Nothing asserted the DB keys the Counters page actually renders.
/// </para>
/// <para>
/// The load-bearing case is <c>retried-jobs</c>: it counts DISTINCT jobs that entered retry, not retry
/// EVENTS, and the two only diverge on a second retry of the same job — which is why a test that retries
/// once cannot tell a correct implementation from one that increments unconditionally.
/// </para>
/// </summary>
[GenerateDatabaseTests]
public abstract class OutcomeMetricsTestsBase : IAsyncLifetime
{
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly Guid WorkerId = Guid.NewGuid();

    private readonly IDatabaseFixture _fixture;

    protected OutcomeMetricsTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

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
    public async Task Retry_FirstAttemptWithDelay_CountsRequeuedRetryAndRetriedJobs()
    {
        // Arrange — production-shaped delays, so the retry lands in Scheduled (the state the breakdown had
        // to learn to treat as a requeue).
        var queue = $"outcome-retry-first-{Guid.NewGuid():N}";
        var jobId = await SeedThrowingJob(queue);
        var worker = CreateWorker(queue, maxRetries: 3, retryDelays: [15, 60, 300]);

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        (await ReadJob(jobId)).CurrentState.ShouldBe(State.Scheduled);

        var counters = await ReadCounters();
        Sum(counters, "stats:requeued").ShouldBe(1);
        Sum(counters, "stats:requeued-retry").ShouldBe(1);
        Sum(counters, "stats:retried-jobs").ShouldBe(1);

        // Every lifetime key must have its hourly sibling, or the Counters chart shows nothing for a key
        // whose total is climbing.
        HourlySum(counters, "stats:requeued-retry").ShouldBe(1);
        HourlySum(counters, "stats:retried-jobs").ShouldBe(1);

        // A requeue is not terminal, so nothing in the failed / deleted family may move.
        Sum(counters, "stats:failed").ShouldBe(0);
        Sum(counters, "stats:deleted").ShouldBe(0);
        AssertAppendOnly(counters);
        AssertUmbrellaIsNotStored(counters);
    }

    [TimedFact]
    public async Task Retry_SecondAttemptOfSameJob_CountsTheEventButNotASecondRetriedJob()
    {
        // Arrange — the distinct-jobs invariant. A job retried twice is TWO retry events but still ONE job
        // that has retried; an implementation that increments retried-jobs unconditionally passes the
        // single-retry test above and fails only here.
        var queue = $"outcome-retry-second-{Guid.NewGuid():N}";
        var jobId = await SeedThrowingJob(queue);
        var worker = CreateWorker(queue, maxRetries: 3, retryDelays: [15, 60, 300]);

        await worker.GetAndProcessJob(CancellationToken.None);

        // The retry is Scheduled into the future and the worker only fetches Enqueued rows (§2.8), so make
        // it eligible again directly rather than waiting out a 15s backoff. The job's RetriedTimes metadata
        // — the input the distinct-job count reads — is untouched by this.
        var reactivateCtx = _fixture.CreateContext();
        var scheduled = await reactivateCtx.Set<Job>().FirstAsync(x => x.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        scheduled.CurrentState = State.Enqueued;
        scheduled.ScheduleTime = DateTime.UtcNow;
        await reactivateCtx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        var counters = await ReadCounters();
        Sum(counters, "stats:requeued-retry").ShouldBe(2, "each retry is its own event");
        Sum(counters, "stats:retried-jobs").ShouldBe(1, "retried-jobs counts distinct jobs, not attempts");
        AssertAppendOnly(counters);
    }

    [TimedFact]
    public async Task Retry_BudgetExhausted_CountsFailedRetryExhausted()
    {
        // Arrange — one retry, taken immediately (Delays = [] ⇒ Enqueued), then exhaustion. Exhaustion used
        // to be signalled by setting NO outcome, making it indistinguishable from a job with no retry policy.
        var queue = $"outcome-retry-exhausted-{Guid.NewGuid():N}";
        var jobId = await SeedThrowingJob(queue);
        var worker = CreateWorker(queue, maxRetries: 1, retryDelays: []);

        await worker.GetAndProcessJob(CancellationToken.None);

        // Act — second attempt has no budget left.
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        (await ReadJob(jobId)).CurrentState.ShouldBe(State.Failed);

        var counters = await ReadCounters();
        Sum(counters, "stats:failed").ShouldBe(1);
        Sum(counters, "stats:failed-retry-exhausted").ShouldBe(1);
        HourlySum(counters, "stats:failed-retry-exhausted").ShouldBe(1);

        // The retry that preceded the exhaustion is still counted — one job, one retry, one terminal failure.
        Sum(counters, "stats:requeued-retry").ShouldBe(1);
        Sum(counters, "stats:retried-jobs").ShouldBe(1);
        AssertAppendOnly(counters);
        AssertUmbrellaIsNotStored(counters);
    }

    [TimedFact]
    public async Task FirstAttemptFailure_WithNoRetryBudget_WritesNoRetriedJobs()
    {
        // Arrange — MaxRetries = 0. The job fails on its first and only attempt, so no job ever entered
        // retry and the distinct-jobs counter must stay at zero. Guards against incrementing it off the
        // reason alone (the exhausted reason IS stamped here — a zero budget is still a spent budget).
        var queue = $"outcome-no-retry-{Guid.NewGuid():N}";
        var jobId = await SeedThrowingJob(queue);
        var worker = CreateWorker(queue, maxRetries: 0, retryDelays: []);

        // Act
        await worker.GetAndProcessJob(CancellationToken.None);

        // Assert
        (await ReadJob(jobId)).CurrentState.ShouldBe(State.Failed);

        var counters = await ReadCounters();
        Sum(counters, "stats:retried-jobs").ShouldBe(0);
        Sum(counters, "stats:requeued").ShouldBe(0);
        Sum(counters, "stats:requeued-retry").ShouldBe(0);
        Sum(counters, "stats:failed").ShouldBe(1);
        AssertAppendOnly(counters);
        AssertUmbrellaIsNotStored(counters);
    }

    private async Task<Guid> SeedThrowingJob(string queue)
    {
        var jobId = Guid.NewGuid();
        var ctx = _fixture.CreateContext();
        ctx.Set<Job>().Add(new Job
        {
            Id = jobId,
            Kind = JobKind.Job,
            CurrentState = State.Enqueued,
            Type = typeof(ThrowExceptionRequest).AssemblyQualifiedName,
            Message = JsonSerializer.Serialize(new ThrowExceptionRequest()),
            CreateTime = DateTime.UtcNow,
            ScheduleTime = DateTime.UtcNow,
            Queue = queue,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        return jobId;
    }

    private async Task<Job> ReadJob(Guid jobId) =>
        await _fixture.CreateContext()
            .Set<Job>()
            .AsNoTracking()
            .FirstAsync(x => x.Id == jobId, Xunit.TestContext.Current.CancellationToken);

    private async Task<List<Counter>> ReadCounters() =>
        await _fixture.CreateContext()
            .Set<Counter>()
            .AsNoTracking()
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

    private WarpWorkerService<TestContext> CreateWorker(string queue, int maxRetries, int[] retryDelays)
    {
        var queues = new[] { queue };
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging(builder => builder.AddProvider(new JobLoggerProvider()));
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddTestServerContext<TestContext>();
        services.AddSingleton<CounterService>();
        services.AddSingleton<MultiHandlerCounter>();
        services.AddSingleton<ActivityCapture>();
        services.AddSingleton(new BarrierSignal());
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());
        services.TryAddSingleton(TimeProvider.System);
        new WarpBuilder<TestContext>(services).AddRetry(o =>
        {
            o.MaxRetries = maxRetries;
            o.Delays = retryDelays;
        });

        var workerConfig = new OptionsWrapper<WarpServerConfiguration>(new WarpServerConfiguration
        {
            WorkerCount = 1,
            ServerId = ServerId,
            Queues = queues,
            EnableHandlerLogging = true,
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

    private static void AssertAppendOnly(List<Counter> counters) =>
        counters.ShouldNotContain(c => c.Value < 0, "stats: counters are append-only — no row may be negative.");

    /// <summary>
    /// The "not Completed" umbrella is DERIVED on read as <c>failed + deleted</c> and deliberately never
    /// stored: ten sites move those two keys, and a stored umbrella maintained at only some of them
    /// under-reports silently. This pins that decision — a reintroduced write fails here.
    /// </summary>
    private static void AssertUmbrellaIsNotStored(List<Counter> counters) =>
        counters.ShouldNotContain(
            c => c.Key.StartsWith("stats:unsuccessful", StringComparison.Ordinal),
            "stats:unsuccessful is derived on read (failed + deleted), never written.");

    private static int Sum(List<Counter> counters, string key) =>
        counters
            .Where(x => string.Equals(x.Key, key, StringComparison.Ordinal))
            .Sum(x => x.Value);

    // Hourly bucket rows are "{key}:{yyyy-MM-dd-HH}". Matching on the prefix keeps the assertion free of
    // wall-clock coupling — a test running across an hour boundary still sums to the same total.
    private static int HourlySum(List<Counter> counters, string key) =>
        counters
            .Where(x => x.Key.StartsWith(key + ":", StringComparison.Ordinal))
            .Sum(x => x.Value);
}
