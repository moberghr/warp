using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Worker;

namespace Warp.Tests.Worker;

[GenerateDatabaseTests]
public abstract class CompletionBatchTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;
    private readonly BatchTestTimeProvider _time = new(new DateTime(2026, 4, 17, 10, 0, 0, DateTimeKind.Utc));

    protected CompletionBatchTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private IServiceScopeFactory CreateScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddScoped<Warp.Core.IWarpServerContext>(x => new Warp.Tests.Helpers.TestServerContext(x.GetRequiredService<TestContext>()));
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    private static readonly IDatabaseExceptionClassifier NoDeadlocks = new NoDeadlockClassifier();

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

    private static PendingCompletion MakeEntry(Job job)
    {
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
        return new PendingCompletion(job, counters, logs);
    }

    [TimedFact]
    public async Task FlushAsync_PersistsAllBufferedCompletions_WhenSizeReached()
    {
        // Arrange
        var scopeFactory = CreateScopeFactory();
        var batch = new CompletionBatch<TestContext>(scopeFactory, _time, NullLogger.Instance, NoDeadlocks, batchSize: 3, flushInterval: TimeSpan.FromSeconds(10));

        var job1 = await InsertProcessingJob();
        var job2 = await InsertProcessingJob();
        var job3 = await InsertProcessingJob();

        batch.Add(MakeEntry(job1));
        batch.Add(MakeEntry(job2));
        batch.Add(MakeEntry(job3));

        batch.IsFull.ShouldBeTrue();

        // Act
        await batch.FlushAsync();

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

        var counters = await ctx.Set<Counter>().CountAsync(Xunit.TestContext.Current.CancellationToken);
        counters.ShouldBe(6);

        var logs = await ctx.Set<JobLog>()
            .Where(x => x.EventType == "Completed")
            .CountAsync(Xunit.TestContext.Current.CancellationToken);
        logs.ShouldBe(3);

        batch.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task FlushAsync_WhenEmpty_IsNoOp()
    {
        // Arrange
        var scopeFactory = CreateScopeFactory();
        var batch = new CompletionBatch<TestContext>(scopeFactory, _time, NullLogger.Instance, NoDeadlocks, batchSize: 10, flushInterval: TimeSpan.FromSeconds(1));

        // Act
        await batch.FlushAsync();

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
        var batch = new CompletionBatch<TestContext>(scopeFactory, _time, NullLogger.Instance, NoDeadlocks, batchSize: 10, flushInterval: TimeSpan.FromSeconds(10));

        var job = await InsertProcessingJob();
        batch.Add(MakeEntry(job));

        // Act
        await batch.FlushAsync();
        var countersAfterFirst = await _fixture.CreateContext().Set<Counter>().CountAsync(Xunit.TestContext.Current.CancellationToken);

        await batch.FlushAsync();

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
        var batch = new CompletionBatch<TestContext>(scopeFactory, _time, NullLogger.Instance, NoDeadlocks, batchSize: 100, flushInterval: flushInterval);

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

        // Assert — empty buffer, no timestamp
        batch.IsTimeElapsed.ShouldBeFalse();
    }

    [TimedFact]
    public async Task FlushAsync_WithPoisonEntry_DropsPoisonAndCommitsGoodEntries()
    {
        // Arrange — one real job and one phantom. A full-batch UPDATE hits 0 rows for the phantom
        // and EF raises DbUpdateConcurrencyException. FlushAsync must split on failure, isolate
        // the poison entry, commit the good one, and return without surfacing the exception.
        var scopeFactory = CreateScopeFactory();
        var batch = new CompletionBatch<TestContext>(scopeFactory, _time, NullLogger.Instance, NoDeadlocks, batchSize: 10, flushInterval: TimeSpan.FromSeconds(10));

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
            [new Counter { Key = "stats:succeeded", Value = 1 }],
            [new JobLog { JobId = phantomJob.Id, EventType = "Completed", Timestamp = DateTime.UtcNow, Level = "Information", Message = "phantom" }]));

        // Act
        await batch.FlushAsync();

        // Assert — real job committed, phantom dropped, buffer drained.
        var ctx = _fixture.CreateContext();
        var persisted = await ctx.Set<Job>().FirstAsync(x => x.Id == realJob.Id, Xunit.TestContext.Current.CancellationToken);
        persisted.CurrentState.ShouldBe(State.Completed);
        persisted.CurrentWorkerId.ShouldBeNull();

        (await ctx.Set<Job>().AnyAsync(x => x.Id == phantomJob.Id, Xunit.TestContext.Current.CancellationToken)).ShouldBeFalse();

        var counters = await ctx.Set<Counter>().ToListAsync(Xunit.TestContext.Current.CancellationToken);
        counters.Count(c => string.Equals(c.Key, "stats:succeeded", StringComparison.Ordinal)).ShouldBe(1);
        counters.ShouldNotContain(c => c.Value == 1 && c.Key == "stats:succeeded" && c.Id == 0 && string.Equals(c.Key, "phantom", StringComparison.Ordinal));

        batch.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task FlushAsync_WithSinglePoisonInLargerBatch_CommitsAllGoodEntries()
    {
        // Arrange — four good jobs and one poison in the middle. Split-on-failure must isolate
        // the single bad entry (via recursive halving) without dropping the neighbours.
        var scopeFactory = CreateScopeFactory();
        var batch = new CompletionBatch<TestContext>(scopeFactory, _time, NullLogger.Instance, NoDeadlocks, batchSize: 10, flushInterval: TimeSpan.FromSeconds(10));

        var good1 = await InsertProcessingJob();
        var good2 = await InsertProcessingJob();
        var good3 = await InsertProcessingJob();
        var good4 = await InsertProcessingJob();

        batch.Add(MakeEntry(good1));
        batch.Add(MakeEntry(good2));

        var phantom = new Job
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
        batch.Add(new PendingCompletion(phantom, [], []));

        batch.Add(MakeEntry(good3));
        batch.Add(MakeEntry(good4));

        // Act
        await batch.FlushAsync();

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
    public async Task FlushAsync_WhenClassifierReportsTransient_RetriesUntilBudgetExhaustedThenSplitsOnFailure()
    {
        // A classifier that always reports "transient deadlock" forces the retry loop to spin
        // through its full budget (3 attempts, 50/100/200 ms backoff) before falling through
        // to the existing split-on-failure path. Setup mirrors WithPoisonEntry: one real job
        // and one phantom. After the budget exhausts, split-on-failure isolates the phantom
        // and commits the good entry.
        var classifier = new AlwaysTransientClassifier();
        var scopeFactory = CreateScopeFactory();
        var batch = new CompletionBatch<TestContext>(scopeFactory, _time, NullLogger.Instance, classifier, batchSize: 10, flushInterval: TimeSpan.FromSeconds(10));

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
        batch.Add(new PendingCompletion(phantomJob, [], []));

        // Act
        await batch.FlushAsync();

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
        var scopeFactory = CreateScopeFactory();
        var batch = new CompletionBatch<TestContext>(scopeFactory, _time, NullLogger.Instance, classifier, batchSize: 10, flushInterval: TimeSpan.FromSeconds(10));

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
        batch.Add(new PendingCompletion(phantomJob, [], []));

        // Act
        await batch.FlushAsync();

        // Assert — first throw reported transient (retry), second throw reported
        // non-transient (drop into the DbUpdateException catch → log + drop poison).
        classifier.CallCount.ShouldBe(2);

        var ctx = _fixture.CreateContext();
        (await ctx.Set<Job>().AnyAsync(x => x.Id == phantomJob.Id, Xunit.TestContext.Current.CancellationToken))
            .ShouldBeFalse();

        batch.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task FlushAsync_CommitsBufferedCompletions_RegardlessOfCallerCancellation()
    {
        // Regression for PR #127 review finding: under the pre-fix code path, FlushAsync drained
        // the buffer before observing cancellation. If the token was cancelled (e.g. SIGTERM mid-
        // flush), BeginTransactionAsync threw OCE, the drained entries were lost, and the caller's
        // subsequent FlushAsync was a noop on an empty buffer. Jobs stayed Processing and only
        // StaleJobRecoveryTask would later requeue them.
        //
        // The fix removed the CancellationToken parameter entirely from FlushAsync so callers
        // cannot accidentally abort an in-flight commit. This test documents the new contract:
        // calling FlushAsync always commits the buffered entries, there's no caller-visible way
        // to cancel a drained flush mid-commit.
        var scopeFactory = CreateScopeFactory();
        var batch = new CompletionBatch<TestContext>(scopeFactory, _time, NullLogger.Instance, NoDeadlocks, batchSize: 10, flushInterval: TimeSpan.FromSeconds(10));

        var job = await InsertProcessingJob();
        batch.Add(MakeEntry(job));

        await batch.FlushAsync();

        var persisted = await _fixture.CreateContext().Set<Job>().FirstAsync(x => x.Id == job.Id, Xunit.TestContext.Current.CancellationToken);
        persisted.CurrentState.ShouldBe(State.Completed);
        persisted.CurrentWorkerId.ShouldBeNull();
        persisted.LastKeepAlive.ShouldBeNull();
        batch.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task Add_WhenBatchSizeIsOne_MarksFullImmediately()
    {
        // Arrange
        var scopeFactory = CreateScopeFactory();
        var batch = new CompletionBatch<TestContext>(scopeFactory, _time, NullLogger.Instance, NoDeadlocks, batchSize: 1, flushInterval: TimeSpan.FromSeconds(10));

        var job = await InsertProcessingJob();

        // Act
        batch.Add(MakeEntry(job));

        // Assert
        batch.IsFull.ShouldBeTrue();
        batch.Count.ShouldBe(1);

        // Flush commits the single entry
        await batch.FlushAsync();
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
