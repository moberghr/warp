using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Core;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.ErrorGrouping;
using Warp.Core.Events;
using Warp.Core.Notifications;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;

namespace Warp.Tests.Worker;

[GenerateDatabaseTests]
public abstract class CompletionBatchTestsBase : IAsyncLifetime
{
    // Counter increments are summed here rather than written as rows. Tests that assert on
    // Counter/Statistic rows flush it with TestTasks.FlushCountersAsync before reading.
    private readonly WarpCounterBuffer _counterBuffer = new();

    private readonly IDatabaseFixture _fixture;
    private readonly BatchTestTimeProvider _time = new(new DateTime(2026, 4, 17, 10, 0, 0, DateTimeKind.Utc));

    protected CompletionBatchTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private IServiceScopeFactory CreateScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddTestServerContext<TestContext>();
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    private static readonly IDatabaseExceptionClassifier NoDeadlocks = new NoDeadlockClassifier();

    // The provider's own statement, as WarpDispatcherWorker passes it.
    private static Task<IReadOnlyCollection<Guid>> CompleteJobs(DbContext context, IReadOnlyList<Job> jobs, IReadOnlyList<JobRunOwner> owners, IReadOnlyList<JobLog> logs, CancellationToken ct) =>
        TestTasks.QueriesFor((TestContext)context).CompleteJobsAsync(context, jobs, owners, logs, ct);

    // The statement failing for any batch that holds one particular job: what a row the database
    // refuses looks like to the batch, which then has to isolate it.
    private static CompletionBatch.CompleteJobs FailingOn(Guid poisonId) =>
        (context, jobs, owners, logs, ct) => jobs.Any(x => x.Id == poisonId)
            ? throw new DbUpdateException("Injected failure: the database refused this job's completion")
            : CompleteJobs(context, jobs, owners, logs, ct);

    private sealed class NoDeadlockClassifier : IDatabaseExceptionClassifier
    {
        public bool IsUniqueConstraintViolation(DbUpdateException ex) => false;

        public bool IsTransientDeadlock(Exception ex) => false;
    }

    private sealed class AlwaysTransientClassifier : IDatabaseExceptionClassifier
    {
        public int CallCount { get; private set; }

        public bool IsUniqueConstraintViolation(DbUpdateException ex) => false;

        public bool IsTransientDeadlock(Exception ex)
        {
            CallCount++;

            return true;
        }
    }

    private sealed class OnceTransientClassifier : IDatabaseExceptionClassifier
    {
        public int CallCount { get; private set; }

        public bool IsUniqueConstraintViolation(DbUpdateException ex) => false;

        public bool IsTransientDeadlock(Exception ex)
        {
            CallCount++;

            return CallCount == 1;
        }
    }

    private async Task<Job> InsertProcessingJob()
    {
        var ctx = _fixture.CreateContext();
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Processing,
            Type = "test",
            Message = "{}",
            CreateTime = _time.GetUtcNow().UtcDateTime,
            ScheduleTime = _time.GetUtcNow().UtcDateTime,
            Queue = "default",
            CurrentWorkerId = Guid.NewGuid(),
            LastKeepAlive = _time.GetUtcNow().UtcDateTime,
        };
        ctx.Set<Job>().Add(job);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        return job;
    }

    // The run as the worker records it before finalization clears the row's worker fields: the claim's
    // stamps on the inserted row, and a worker of its own.
    private static JobRunOwner OwnerOf(Job job) => JobRunOwner.Of(job, Guid.NewGuid());

    // A completion for a job with no row.
    private static PendingCompletion Phantom()
    {
        var job = new Job { Id = Guid.NewGuid() };

        return new PendingCompletion(job, OwnerOf(job), [], []);
    }

    private static PendingCompletion MakeEntry(Job job)
    {
        var owner = OwnerOf(job);
        job.CurrentState = State.Completed;
        job.CurrentWorkerId = null;
        job.LastKeepAlive = null;
        job.ExpireAt = DateTime.UtcNow.AddHours(1);

        var counters = new List<Counter>
        {
            new() { Key = "stats:succeeded", Value = 1 },
            new() { Key = "stats:succeeded:2026-04-17-10", Value = 1 },
        };
        var logs = new List<JobLog>
        {
            new()
            {
                JobId = job.Id,
                EventType = "Completed",
                Timestamp = DateTime.UtcNow,
                Level = "Information",
                Message = $"Job {job.Id} completed",
            },
        };
        return new PendingCompletion(job, owner, counters, logs);
    }

    [TimedFact]
    public async Task SlotReleased_GroupIdleButTheNextJobClaimedBeforeTheWorkerSaysSo_WritesTheCompletion()
    {
        // Arrange — one slot, no prefetch, and a flush timer far beyond the test: only the idle trigger can
        // write this completion. Found on CI: the dispatcher, woken by the freed slot, claimed the next job
        // before the worker told the flusher the group was idle, so the flusher read a busy group and left
        // the completion for the timer.
        var batch = new CompletionBatch(CreateScopeFactory(), _time, NullLogger.Instance, NoDeadlocks, batchSize: 50, flushInterval: TimeSpan.FromMinutes(5), _counterBuffer, CompleteJobs);
        var availability = new DispatcherWorkerAvailability(workerCount: 1, prefetchCount: 0);
        var flusher = new CompletionFlusher<TestContext>(batch, availability, _time, new ServerTaskSignals<TestContext>(), new NullNotificationTransport(), NullLogger.Instance, DispatcherDiagnostics.GroupTag(Guid.Empty));
        flusher.Start();
        var job = await InsertProcessingJob();
        availability.Reserve(1);
        await flusher.AddAsync(MakeEntry(job));

        // Act — the worker returns its slot, the dispatcher claims the next job, then the worker reports.
        var remaining = availability.Release();
        availability.Reserve(1);
        flusher.SlotReleased(remaining);

        // Assert
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var state = State.Processing;
        while (DateTime.UtcNow < deadline)
        {
            state = await _fixture.CreateContext().Set<Job>()
                .Where(x => x.Id == job.Id)
                .Select(x => x.CurrentState)
                .SingleAsync(Xunit.TestContext.Current.CancellationToken);
            if (state == State.Completed)
            {
                break;
            }

            await Task.Yield();
        }

        await flusher.StopAsync(Xunit.TestContext.Current.CancellationToken);
        state.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task StopAsync_AWriteThatHangs_ReturnsWhenItsTokenFires()
    {
        // Arrange — the loop's write hangs on a database that never answers. Shutdown must not wait for it
        // past the host's shutdown token.
        var entered = new SemaphoreSlim(0);
        var release = new TaskCompletionSource();
        async Task<IReadOnlyCollection<Guid>> Hang(DbContext context, IReadOnlyList<Job> jobs, IReadOnlyList<JobRunOwner> owners, IReadOnlyList<JobLog> logs, CancellationToken ct)
        {
            entered.Release();
            await release.Task.WaitAsync(ct);

            return [];
        }

        var batch = new CompletionBatch(CreateScopeFactory(), _time, NullLogger.Instance, NoDeadlocks, batchSize: 50, flushInterval: TimeSpan.FromMinutes(5), _counterBuffer, Hang);
        var availability = new DispatcherWorkerAvailability(workerCount: 1, prefetchCount: 0);
        var flusher = new CompletionFlusher<TestContext>(batch, availability, _time, new ServerTaskSignals<TestContext>(), new NullNotificationTransport(), NullLogger.Instance, DispatcherDiagnostics.GroupTag(Guid.Empty));
        flusher.Start();
        try
        {
            await flusher.AddAsync(MakeEntry(await InsertProcessingJob()));
            flusher.SlotReleased(0);
            (await entered.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken)).ShouldBeTrue("the write never started");

            // Act
            using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var stopping = flusher.StopAsync(shutdown.Token);

            // Assert
            (await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))).ShouldBe(stopping, "StopAsync waited for the hung write past its token");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TimedFact]
    public async Task AddAsync_GroupHoldsTheBackpressureThreshold_WaitsUntilTheWriteInFlightEnds()
    {
        // Arrange — batch size 1, so the threshold is four unwritten completions. The first is drained into
        // a write that hangs; the next three are buffered behind it and return at once; the fourth brings the
        // group to the threshold and must wait for the hung write, not grow the buffer further (#348).
        var entered = new SemaphoreSlim(0);
        var release = new TaskCompletionSource();
        async Task<IReadOnlyCollection<Guid>> Hang(DbContext context, IReadOnlyList<Job> jobs, IReadOnlyList<JobRunOwner> owners, IReadOnlyList<JobLog> logs, CancellationToken ct)
        {
            entered.Release();
            await release.Task.WaitAsync(ct);

            return [];
        }

        var batch = new CompletionBatch(CreateScopeFactory(), _time, NullLogger.Instance, NoDeadlocks, batchSize: 1, flushInterval: TimeSpan.FromMinutes(5), _counterBuffer, Hang);
        var availability = new DispatcherWorkerAvailability(workerCount: 4, prefetchCount: 0);
        var flusher = new CompletionFlusher<TestContext>(batch, availability, _time, new ServerTaskSignals<TestContext>(), new NullNotificationTransport(), NullLogger.Instance, DispatcherDiagnostics.GroupTag(Guid.Empty));
        flusher.Start();
        try
        {
            await flusher.AddAsync(Phantom(), Xunit.TestContext.Current.CancellationToken);
            (await entered.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken)).ShouldBeTrue("the write never started");
            for (var i = 0; i < 3; i++)
            {
                // Below the threshold: returns without waiting for the hung write (a wait would time the test out).
                await flusher.AddAsync(Phantom(), Xunit.TestContext.Current.CancellationToken);
            }

            // Act
            var waiting = flusher.AddAsync(Phantom(), Xunit.TestContext.Current.CancellationToken).AsTask();

            // Assert
            waiting.IsCompleted.ShouldBeFalse("the worker took its next job with the group at the threshold");
            release.TrySetResult();
            (await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))).ShouldBe(waiting, "the worker was not released when the write ended");
        }
        finally
        {
            release.TrySetResult();
            await flusher.StopAsync(Xunit.TestContext.Current.CancellationToken);
        }
    }

    [TimedFact]
    public async Task AddAsync_WaitingOnAHungWrite_ReturnsWhenTheWorkerStops()
    {
        // Arrange — the workers are stopped before the flusher, so a worker held by backpressure must let go
        // on its stopping token, or a hung database would hang the shutdown. Its completion stays buffered.
        var entered = new SemaphoreSlim(0);
        var release = new TaskCompletionSource();
        async Task<IReadOnlyCollection<Guid>> Hang(DbContext context, IReadOnlyList<Job> jobs, IReadOnlyList<JobRunOwner> owners, IReadOnlyList<JobLog> logs, CancellationToken ct)
        {
            entered.Release();
            await release.Task.WaitAsync(ct);

            return [];
        }

        var batch = new CompletionBatch(CreateScopeFactory(), _time, NullLogger.Instance, NoDeadlocks, batchSize: 1, flushInterval: TimeSpan.FromMinutes(5), _counterBuffer, Hang);
        var availability = new DispatcherWorkerAvailability(workerCount: 4, prefetchCount: 0);
        var flusher = new CompletionFlusher<TestContext>(batch, availability, _time, new ServerTaskSignals<TestContext>(), new NullNotificationTransport(), NullLogger.Instance, DispatcherDiagnostics.GroupTag(Guid.Empty));
        flusher.Start();
        using var stopping = new CancellationTokenSource();
        try
        {
            await flusher.AddAsync(Phantom(), stopping.Token);
            (await entered.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken)).ShouldBeTrue("the write never started");
            for (var i = 0; i < 3; i++)
            {
                await flusher.AddAsync(Phantom(), stopping.Token);
            }

            var waiting = flusher.AddAsync(Phantom(), stopping.Token).AsTask();
            waiting.IsCompleted.ShouldBeFalse();

            // Act
            await stopping.CancelAsync();

            // Assert
            (await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken))).ShouldBe(waiting, "the stopping worker kept waiting on the hung write");
            batch.Count.ShouldBe(4);
        }
        finally
        {
            release.TrySetResult();
            await flusher.StopAsync(Xunit.TestContext.Current.CancellationToken);
        }
    }

    [TimedFact]
    public async Task StopAsync_ShutdownTokenAlreadyFired_StillWritesWhatIsBuffered()
    {
        // Arrange — the host's token has often fired by the time the workers have stopped; the final flush
        // has a budget of its own, or every completion still buffered is dropped.
        var batch = new CompletionBatch(CreateScopeFactory(), _time, NullLogger.Instance, NoDeadlocks, batchSize: 50, flushInterval: TimeSpan.FromMinutes(5), _counterBuffer, CompleteJobs);
        var availability = new DispatcherWorkerAvailability(workerCount: 1, prefetchCount: 0);
        var flusher = new CompletionFlusher<TestContext>(batch, availability, _time, new ServerTaskSignals<TestContext>(), new NullNotificationTransport(), NullLogger.Instance, DispatcherDiagnostics.GroupTag(Guid.Empty));
        flusher.Start();
        var job = await InsertProcessingJob();
        await flusher.AddAsync(MakeEntry(job));

        // Act
        await flusher.StopAsync(new CancellationToken(canceled: true));

        // Assert
        var state = await _fixture.CreateContext().Set<Job>()
            .Where(x => x.Id == job.Id)
            .Select(x => x.CurrentState)
            .SingleAsync(Xunit.TestContext.Current.CancellationToken);
        state.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task AddAsync_AfterStopAsync_WritesTheCompletion()
    {
        // Arrange — a handler that outlived the shutdown returns after the final flush: its completion must
        // not land in a buffer nothing will write.
        var batch = new CompletionBatch(CreateScopeFactory(), _time, NullLogger.Instance, NoDeadlocks, batchSize: 50, flushInterval: TimeSpan.FromMinutes(5), _counterBuffer, CompleteJobs);
        var availability = new DispatcherWorkerAvailability(workerCount: 1, prefetchCount: 0);
        var flusher = new CompletionFlusher<TestContext>(batch, availability, _time, new ServerTaskSignals<TestContext>(), new NullNotificationTransport(), NullLogger.Instance, DispatcherDiagnostics.GroupTag(Guid.Empty));
        flusher.Start();
        await flusher.StopAsync(Xunit.TestContext.Current.CancellationToken);
        var job = await InsertProcessingJob();

        // Act
        await flusher.AddAsync(MakeEntry(job));

        // Assert
        var state = await _fixture.CreateContext().Set<Job>()
            .Where(x => x.Id == job.Id)
            .Select(x => x.CurrentState)
            .SingleAsync(Xunit.TestContext.Current.CancellationToken);
        state.ShouldBe(State.Completed);
    }

    [TimedFact]
    public async Task FlushAsync_MoreThanFourBatchesBuffered_WritesStatementsOfAtMostFourBatches()
    {
        // Arrange — completions keep arriving while a flush is written, so a buffer can hold more than its
        // size; one statement for all of them made the statement unbounded.
        var sizes = new List<int>();
        Task<IReadOnlyCollection<Guid>> Record(DbContext context, IReadOnlyList<Job> jobs, IReadOnlyList<JobRunOwner> owners, IReadOnlyList<JobLog> logs, CancellationToken ct)
        {
            sizes.Add(jobs.Count);

            return CompleteJobs(context, jobs, owners, logs, ct);
        }

        var batch = new CompletionBatch(CreateScopeFactory(), _time, NullLogger.Instance, NoDeadlocks, batchSize: 2, flushInterval: TimeSpan.FromMinutes(5), _counterBuffer, Record);
        for (var i = 0; i < 9; i++)
        {
            batch.Add(MakeEntry(await InsertProcessingJob()));
        }

        // Act
        await batch.FlushAsync();

        // Assert
        sizes.ShouldBe([8, 1]);
    }

    [TimedFact]
    public async Task FlushAsync_PersistsAllBufferedCompletions_WhenSizeReached()
    {
        // Arrange
        var scopeFactory = CreateScopeFactory();
        var batch = new CompletionBatch(scopeFactory, _time, NullLogger.Instance, NoDeadlocks, batchSize: 3, flushInterval: TimeSpan.FromSeconds(10), _counterBuffer, CompleteJobs);

        var job1 = await InsertProcessingJob();
        var job2 = await InsertProcessingJob();
        var job3 = await InsertProcessingJob();

        batch.Add(MakeEntry(job1));
        batch.Add(MakeEntry(job2));
        batch.Add(MakeEntry(job3));

        batch.IsFull.ShouldBeTrue();

        // Act
        await batch.FlushAsync();
        await TestTasks.FlushCountersAsync(_counterBuffer, _fixture.CreateContext());

        // Assert
        var ctx = _fixture.CreateContext();
        var jobs = await ctx.Set<Job>()
            .Where(x => x.Id == job1.Id || x.Id == job2.Id || x.Id == job3.Id)
            .OrderBy(x => x.Id)
            .AsNoTracking()
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        jobs.Count.ShouldBe(3);
        foreach (var job in jobs)
        {
            job.CurrentState.ShouldBe(State.Completed);
            job.CurrentWorkerId.ShouldBeNull();
            job.LastKeepAlive.ShouldBeNull();
        }

        // Increments are summed per key before being written, so three completions produce TWO rows —
        // the lifetime total and its hourly bucket — each carrying 3, rather than six rows of 1.
        var counters = await ctx.Set<Counter>()
            .AsNoTracking()
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        counters.Count.ShouldBe(2);
        counters.Sum(x => x.Value).ShouldBe(6);

        var logs = await ctx.Set<JobLog>()
            .Where(x => x.EventType == "Completed")
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        logs.ShouldBe(3);

        batch.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task FlushAsync_PersistsErrorOccurrence_ForFailedCompletion()
    {
        // Regression: dispatcher-mode failures must feed the error-grouping inbox (§8.29). The single-worker
        // path writes the ErrorOccurrence in FinalizeJobState; the dispatcher carries it on PendingCompletion
        // and CompletionBatch persists it in the completion transaction. This was missed initially (only the
        // single-worker path was wired) and surfaced in the demo (UseDispatcher = true → zero issues).
        var scopeFactory = CreateScopeFactory();
        var batch = new CompletionBatch(scopeFactory, _time, NullLogger.Instance, NoDeadlocks, batchSize: 1, flushInterval: TimeSpan.FromSeconds(10), _counterBuffer, CompleteJobs);

        var job = await InsertProcessingJob();
        var owner = OwnerOf(job);
        job.CurrentState = State.Failed;
        job.CurrentWorkerId = null;
        job.LastKeepAlive = null;

        var occurrence = ErrorOccurrenceFactory.FromException(
            ErrorSource.Job,
            new InvalidOperationException("boom on order 42"),
            "TestRequest",
            job.TraceId,
            application: null,
            _time.GetUtcNow().UtcDateTime);

        batch.Add(new PendingCompletion(job, owner, [], [], occurrence));

        await batch.FlushAsync();
        await TestTasks.FlushCountersAsync(_counterBuffer, _fixture.CreateContext());

        var ctx = _fixture.CreateContext();
        var persisted = (await ctx.Set<ErrorOccurrence>().AsNoTracking().ToListAsync(Xunit.TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        persisted.Source.ShouldBe(ErrorSource.Job);
        persisted.ExceptionType.ShouldBe("System.InvalidOperationException");
        persisted.Culprit.ShouldBe("TestRequest");
        batch.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task FlushAsync_WhenEmpty_IsNoOp()
    {
        // Arrange
        var scopeFactory = CreateScopeFactory();
        var batch = new CompletionBatch(scopeFactory, _time, NullLogger.Instance, NoDeadlocks, batchSize: 10, flushInterval: TimeSpan.FromSeconds(1), _counterBuffer, CompleteJobs);

        // Act
        await batch.FlushAsync();
        await TestTasks.FlushCountersAsync(_counterBuffer, _fixture.CreateContext());

        // Assert
        var ctx = _fixture.CreateContext();
        (await ctx.Set<Counter>().CountAsync(Xunit.TestContext.Current.CancellationToken)).ShouldBe(0);
        (await ctx.Set<JobLog>().CountAsync(Xunit.TestContext.Current.CancellationToken)).ShouldBe(0);
        batch.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task FlushAsync_WhenCalledTwice_SecondIsNoOp()
    {
        // Arrange
        var scopeFactory = CreateScopeFactory();
        var batch = new CompletionBatch(scopeFactory, _time, NullLogger.Instance, NoDeadlocks, batchSize: 10, flushInterval: TimeSpan.FromSeconds(10), _counterBuffer, CompleteJobs);

        var job = await InsertProcessingJob();
        batch.Add(MakeEntry(job));

        // Act
        await batch.FlushAsync();
        await TestTasks.FlushCountersAsync(_counterBuffer, _fixture.CreateContext());
        var countersAfterFirst = await _fixture.CreateContext().Set<Counter>().CountAsync(Xunit.TestContext.Current.CancellationToken);

        await batch.FlushAsync();
        await TestTasks.FlushCountersAsync(_counterBuffer, _fixture.CreateContext());

        // Assert
        var countersAfterSecond = await _fixture.CreateContext().Set<Counter>().CountAsync(Xunit.TestContext.Current.CancellationToken);
        countersAfterSecond.ShouldBe(countersAfterFirst);
        batch.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task IsTimeElapsed_OnlyReturnsTrueAfterInterval()
    {
        // Arrange
        var scopeFactory = CreateScopeFactory();
        var flushInterval = TimeSpan.FromMilliseconds(100);
        var batch = new CompletionBatch(scopeFactory, _time, NullLogger.Instance, NoDeadlocks, batchSize: 100, flushInterval: flushInterval, _counterBuffer, CompleteJobs);

        // Assert empty — no timestamp yet
        batch.IsTimeElapsed.ShouldBeFalse();

        var job = await InsertProcessingJob();
        batch.Add(MakeEntry(job));

        // Just after add — not elapsed
        batch.IsTimeElapsed.ShouldBeFalse();

        // Advance time past interval
        _time.Advance(flushInterval + TimeSpan.FromMilliseconds(1));
        batch.IsTimeElapsed.ShouldBeTrue();

        // Act — flush resets the timestamp
        await batch.FlushAsync();
        await TestTasks.FlushCountersAsync(_counterBuffer, _fixture.CreateContext());

        // Assert — empty buffer, no timestamp
        batch.IsTimeElapsed.ShouldBeFalse();
    }

    [TimedFact]
    public async Task FlushAsync_WithPoisonEntry_DropsPoisonAndCommitsGoodEntries()
    {
        // Arrange — one real job and one whose row does not exist (deleted while it ran). The statement
        // skips the missing one with its log rows, the good one commits, and the missing one counts
        // nothing.
        var scopeFactory = CreateScopeFactory();
        var batch = new CompletionBatch(scopeFactory, _time, NullLogger.Instance, NoDeadlocks, batchSize: 10, flushInterval: TimeSpan.FromSeconds(10), _counterBuffer, CompleteJobs);

        var realJob = await InsertProcessingJob();
        batch.Add(MakeEntry(realJob));

        var phantomJob = new Job
        {
            Id = Guid.NewGuid(),
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            Type = "test",
            Message = "{}",
            CreateTime = _time.GetUtcNow().UtcDateTime,
            ScheduleTime = _time.GetUtcNow().UtcDateTime,
            Queue = "default",
        };
        batch.Add(new PendingCompletion(
            phantomJob,
            OwnerOf(phantomJob),
            [new Counter { Key = "stats:succeeded", Value = 1 }],
            [new JobLog { JobId = phantomJob.Id, EventType = "Completed", Timestamp = DateTime.UtcNow, Level = "Information", Message = "phantom" }]));

        // Act
        await batch.FlushAsync();
        await TestTasks.FlushCountersAsync(_counterBuffer, _fixture.CreateContext());

        // Assert — real job committed, phantom dropped, buffer drained.
        var ctx = _fixture.CreateContext();
        var persisted = await ctx.Set<Job>().FirstAsync(x => x.Id == realJob.Id, Xunit.TestContext.Current.CancellationToken);
        persisted.CurrentState.ShouldBe(State.Completed);
        persisted.CurrentWorkerId.ShouldBeNull();

        (await ctx.Set<Job>().AnyAsync(x => x.Id == phantomJob.Id, Xunit.TestContext.Current.CancellationToken)).ShouldBeFalse();

        // The summed VALUE, not the row count: the buffer collapses a key to one row per flush, so
        // a phantom increment that got through would still be one row, with a value of 2.
        var succeeded = await ctx.Set<Counter>()
            .Where(x => x.Key == "stats:succeeded")
            .SumAsync(x => x.Value, Xunit.TestContext.Current.CancellationToken);
        succeeded.ShouldBe(1);

        (await ctx.Set<JobLog>().AnyAsync(x => x.JobId == phantomJob.Id, Xunit.TestContext.Current.CancellationToken)).ShouldBeFalse();

        batch.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task FlushAsync_WithSinglePoisonInLargerBatch_CommitsAllGoodEntries()
    {
        // Arrange — four good jobs and one poison in the middle. Split-on-failure must isolate
        // the single bad entry (via recursive halving) without dropping the neighbours.
        var poisonId = Guid.NewGuid();
        var scopeFactory = CreateScopeFactory();
        var batch = new CompletionBatch(scopeFactory, _time, NullLogger.Instance, NoDeadlocks, batchSize: 10, flushInterval: TimeSpan.FromSeconds(10), _counterBuffer, FailingOn(poisonId));

        var good1 = await InsertProcessingJob();
        var good2 = await InsertProcessingJob();
        var good3 = await InsertProcessingJob();
        var good4 = await InsertProcessingJob();

        batch.Add(MakeEntry(good1));
        batch.Add(MakeEntry(good2));

        var phantom = new Job
        {
            Id = poisonId,
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            Type = "test",
            Message = "{}",
            CreateTime = _time.GetUtcNow().UtcDateTime,
            ScheduleTime = _time.GetUtcNow().UtcDateTime,
            Queue = "default",
        };
        batch.Add(new PendingCompletion(phantom, OwnerOf(phantom), [], []));

        batch.Add(MakeEntry(good3));
        batch.Add(MakeEntry(good4));

        // Act
        await batch.FlushAsync();
        await TestTasks.FlushCountersAsync(_counterBuffer, _fixture.CreateContext());

        // Assert — all four real jobs commit as Completed; phantom is gone.
        var ctx = _fixture.CreateContext();
        var committed = await ctx.Set<Job>()
            .Where(x => x.Id == good1.Id || x.Id == good2.Id || x.Id == good3.Id || x.Id == good4.Id)
            .CountAsync(x => x.CurrentState == State.Completed, Xunit.TestContext.Current.CancellationToken);
        committed.ShouldBe(4);

        (await ctx.Set<Job>().AnyAsync(x => x.Id == phantom.Id, Xunit.TestContext.Current.CancellationToken)).ShouldBeFalse();

        batch.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task FlushAsync_TransientDbExceptionOnTheFirstAttempt_CompletesEveryJob()
    {
        // Arrange: a command timeout or a dropped connection on the raw completion statement is a transient
        // DbException, not a DbUpdateException. It escaped the batch, which abandoned every drained job.
        var attempts = 0;
        Task<IReadOnlyCollection<Guid>> FailOnce(DbContext context, IReadOnlyList<Job> jobs, IReadOnlyList<JobRunOwner> owners, IReadOnlyList<JobLog> logs, CancellationToken ct) =>
            Interlocked.Increment(ref attempts) == 1
                ? throw new TransientDbException()
                : CompleteJobs(context, jobs, owners, logs, ct);
        var batch = new CompletionBatch(CreateScopeFactory(), _time, NullLogger.Instance, NoDeadlocks, batchSize: 10, flushInterval: TimeSpan.FromSeconds(10), _counterBuffer, FailOnce);
        var first = await InsertProcessingJob();
        var second = await InsertProcessingJob();
        batch.Add(MakeEntry(first));
        batch.Add(MakeEntry(second));

        // Act
        await batch.FlushAsync();
        await TestTasks.FlushCountersAsync(_counterBuffer, _fixture.CreateContext());

        // Assert
        var ctx = _fixture.CreateContext();
        (await ctx.Set<Job>()
            .Where(x => x.Id == first.Id || x.Id == second.Id)
            .CountAsync(x => x.CurrentState == State.Completed, Xunit.TestContext.Current.CancellationToken))
            .ShouldBe(2);
        (await ctx.Set<Counter>().SumAsync(x => x.Value, Xunit.TestContext.Current.CancellationToken)).ShouldBe(4);
    }

    private sealed class TransientDbException : System.Data.Common.DbException
    {
        public TransientDbException()
            : base("Injected transient failure: the command timed out")
        {
        }

        public TransientDbException(string message)
            : base(message)
        {
        }

        public TransientDbException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public TransientDbException(string message, int errorCode)
            : base(message, errorCode)
        {
        }

        public override bool IsTransient => true;
    }

    [TimedFact]
    public async Task FlushAsync_WhenClassifierReportsTransient_RetriesUntilBudgetExhaustedThenSplitsOnFailure()
    {
        // A classifier that always reports "transient deadlock" forces the retry loop to spin
        // through its full budget (3 attempts, 50/100/200 ms backoff) before falling through
        // to the existing split-on-failure path. Setup mirrors WithPoisonEntry: one real job
        // and one phantom. After the budget exhausts, split-on-failure isolates the phantom
        // and commits the good entry.
        var classifier = new AlwaysTransientClassifier();
        var poisonId = Guid.NewGuid();
        var scopeFactory = CreateScopeFactory();
        var batch = new CompletionBatch(scopeFactory, _time, NullLogger.Instance, classifier, batchSize: 10, flushInterval: TimeSpan.FromSeconds(10), _counterBuffer, FailingOn(poisonId));

        var realJob = await InsertProcessingJob();
        batch.Add(MakeEntry(realJob));

        var phantomJob = new Job
        {
            Id = poisonId,
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            Type = "test",
            Message = "{}",
            CreateTime = _time.GetUtcNow().UtcDateTime,
            ScheduleTime = _time.GetUtcNow().UtcDateTime,
            Queue = "default",
        };
        batch.Add(new PendingCompletion(phantomJob, OwnerOf(phantomJob), [], []));

        // Act
        await batch.FlushAsync();
        await TestTasks.FlushCountersAsync(_counterBuffer, _fixture.CreateContext());

        // Assert — at least one full budget exhaustion (3 calls in the initial pass; more
        // during the recursive phantom split). Lower-bound rather than exact so the
        // assertion stays stable across implementation tweaks to the split recursion.
        classifier.CallCount.ShouldBeGreaterThanOrEqualTo(3);

        var ctx = _fixture.CreateContext();
        (await ctx.Set<Job>().FirstAsync(x => x.Id == realJob.Id, Xunit.TestContext.Current.CancellationToken))
            .CurrentState.ShouldBe(State.Completed);
        (await ctx.Set<Job>().AnyAsync(x => x.Id == phantomJob.Id, Xunit.TestContext.Current.CancellationToken))
            .ShouldBeFalse();

        batch.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task FlushAsync_WhenClassifierStopsReportingTransient_ExitsRetryAndSplitsOnFailure()
    {
        // The classifier is consulted on every throw, not once. When the first throw is
        // reported transient but the next throw is reported non-transient, the retry loop
        // exits immediately and falls through to split-on-failure. Phantom-alone setup so
        // the assertion is "poison dropped, classifier called exactly twice" — no recursive
        // split to distort the call count.
        var classifier = new OnceTransientClassifier();
        var poisonId = Guid.NewGuid();
        var scopeFactory = CreateScopeFactory();
        var batch = new CompletionBatch(scopeFactory, _time, NullLogger.Instance, classifier, batchSize: 10, flushInterval: TimeSpan.FromSeconds(10), _counterBuffer, FailingOn(poisonId));

        var phantomJob = new Job
        {
            Id = poisonId,
            Kind = JobKind.Job,
            CurrentState = State.Completed,
            Type = "test",
            Message = "{}",
            CreateTime = _time.GetUtcNow().UtcDateTime,
            ScheduleTime = _time.GetUtcNow().UtcDateTime,
            Queue = "default",
        };
        batch.Add(new PendingCompletion(phantomJob, OwnerOf(phantomJob), [], []));

        // Act
        await batch.FlushAsync();
        await TestTasks.FlushCountersAsync(_counterBuffer, _fixture.CreateContext());

        // Assert — first throw reported transient (retry), second throw reported
        // non-transient (drop into the DbUpdateException catch → log + drop poison).
        classifier.CallCount.ShouldBe(2);

        var ctx = _fixture.CreateContext();
        (await ctx.Set<Job>().AnyAsync(x => x.Id == phantomJob.Id, Xunit.TestContext.Current.CancellationToken))
            .ShouldBeFalse();

        batch.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task FlushAsync_WhenTheDatabaseIsUnreachable_ThrowsAndLeavesTheRowsProcessing()
    {
        // Not a DbUpdateException, so there is nothing to split or retry: the failure reaches the
        // caller, the buffer is already drained, and the rows stay Processing for StaleJobRecovery.
        // A disposed context stands in for a connection that cannot be opened.
        var job = await InsertProcessingJob();
        var unreachable = _fixture.CreateContext();
        await unreachable.DisposeAsync();

        var services = new ServiceCollection();
        services.AddScoped<IWarpServerContext>(_ => new TestServerContext(unreachable));
        await using var provider = services.BuildServiceProvider();

        var batch = new CompletionBatch(provider.GetRequiredService<IServiceScopeFactory>(), _time, NullLogger.Instance, NoDeadlocks, batchSize: 10, flushInterval: TimeSpan.FromSeconds(10), _counterBuffer, CompleteJobs);
        batch.Add(MakeEntry(job));

        // Act
        await Should.ThrowAsync<ObjectDisposedException>(batch.FlushAsync);
        await TestTasks.FlushCountersAsync(_counterBuffer, _fixture.CreateContext());

        // Assert
        batch.Count.ShouldBe(0);

        var ctx = _fixture.CreateContext();
        var persisted = await ctx.Set<Job>().FirstAsync(x => x.Id == job.Id, Xunit.TestContext.Current.CancellationToken);
        persisted.CurrentState.ShouldBe(State.Processing);

        // Nothing committed, so nothing was counted.
        (await ctx.Set<Counter>().AnyAsync(Xunit.TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task Add_WhileAnotherThreadDrains_NoCompletionIsLostOrSeenTwice()
    {
        // The shutdown flush (WarpDispatcherWorker.StopAsync) can run on the host's thread while the
        // worker loop is still adding. The interleaving that loses an entry sits between two statements
        // inside the drain, where no handler can be pinned, so this one test does what the rest of the
        // suite avoids: it runs the two callers against each other many times and counts. Every added
        // completion must come out of exactly one drain. Without the lock it loses entries or throws.
        const int total = 200_000;
        var batch = new CompletionBatch(CreateScopeFactory(), _time, NullLogger.Instance, NoDeadlocks, batchSize: 10, flushInterval: TimeSpan.FromSeconds(10), _counterBuffer, CompleteJobs);
        var entries = Enumerable.Range(0, total)
            .Select(_ => Phantom())
            .ToArray();

        var drained = new List<PendingCompletion>(total);
        var adding = Task.Run(
            () =>
            {
                foreach (var entry in entries)
                {
                    batch.Add(entry);
                }
            },
            Xunit.TestContext.Current.CancellationToken);

        while (!adding.IsCompleted)
        {
            drained.AddRange(batch.Drain());
        }

        await adding;
        drained.AddRange(batch.Drain());

        drained.Count.ShouldBe(total);
        drained.Select(x => x.Job.Id).Distinct().Count().ShouldBe(total);
        batch.Count.ShouldBe(0);
    }

    [TimedFact]
    public void Holds_ABufferDrainedEarlyWhileANewOneWaits_OnlyTheNewOne()
    {
        // The flush timer started for a buffer must not flush the next one. Under load a buffer is
        // usually drained early (full, or the group idle) and the next starts at once, so its
        // predecessor's timer fired into a buffer a moment old: flushes ran twice as often with half
        // as many jobs, and throughput fell 9-20% on the CI benchmark.
        var batch = new CompletionBatch(CreateScopeFactory(), _time, NullLogger.Instance, NoDeadlocks, batchSize: 10, flushInterval: TimeSpan.FromSeconds(10), _counterBuffer, CompleteJobs);
        batch.Add(Phantom(), out var first);
        batch.Drain();
        batch.Add(Phantom(), out var second);

        // Act
        var holdsFirst = batch.Holds(first);
        var holdsSecond = batch.Holds(second);

        // Assert
        holdsFirst.ShouldBeFalse();
        holdsSecond.ShouldBeTrue();
    }

    // FlushAsync takes no CancellationToken on purpose (PR #127): a caller that could cancel a drained
    // flush mid-commit lost the drained entries and left their jobs Processing for StaleJobRecovery.
    // The signature is the guard; a test that calls it and checks the commit was a copy of
    // FlushAsync_PersistsAllBufferedCompletions_WhenSizeReached.
    [TimedFact]
    public async Task Add_WhenBatchSizeIsOne_MarksFullImmediately()
    {
        // Arrange
        var scopeFactory = CreateScopeFactory();
        var batch = new CompletionBatch(scopeFactory, _time, NullLogger.Instance, NoDeadlocks, batchSize: 1, flushInterval: TimeSpan.FromSeconds(10), _counterBuffer, CompleteJobs);

        var job = await InsertProcessingJob();

        // Act
        batch.Add(MakeEntry(job));

        // Assert
        batch.IsFull.ShouldBeTrue();
        batch.Count.ShouldBe(1);

        // Flush commits the single entry
        await batch.FlushAsync();
        await TestTasks.FlushCountersAsync(_counterBuffer, _fixture.CreateContext());
        var persisted = await _fixture.CreateContext().Set<Job>().FirstAsync(x => x.Id == job.Id, Xunit.TestContext.Current.CancellationToken);
        persisted.CurrentState.ShouldBe(State.Completed);
    }
}

internal sealed class BatchTestTimeProvider(DateTime start) : TimeProvider
{
    private DateTime _now = DateTime.SpecifyKind(start, DateTimeKind.Utc);

    public override DateTimeOffset GetUtcNow() => new(_now, TimeSpan.Zero);

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
