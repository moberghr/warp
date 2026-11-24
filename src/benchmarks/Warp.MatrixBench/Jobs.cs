using Warp.Core.Concurrency;
using Warp.Core.Handlers;
using Warp.Core.RateLimit;
using Warp.Core.Timeout;

namespace Warp.MatrixBench;

// Every handler here records its own execution by job id before doing anything else, so a job that runs
// twice is caught in-process however the database ends up. They touch no DbContext: on main the worker
// saved the handler's context and on the branch it does not, and a handler relying on either would
// measure that difference instead of the library's.
public static class Workloads
{
    public const string Empty = "empty";

    public const string Db = "db";

    public const string Mixed = "mixed";
}

/// <summary>What each handler records itself as, and how many times one job of that kind may run.</summary>
public static class ExecutionKinds
{
    public const string Empty = "empty";
    public const string Db = "db";
    public const string MessageA = "message-a";
    public const string MessageB = "message-b";
    public const string MessageC = "message-c";
    public const string BatchChild = "batch-child";
    public const string ContinuationParent = "continuation-parent";
    public const string ContinuationChild = "continuation-child";
    public const string Failing = "failing";
    public const string Scheduled = "scheduled";
    public const string Recurring = "recurring";
    public const string Mutex = "mutex";
    public const string Semaphore = "semaphore";
    public const string RateLimited = "rate-limited";
    public const string Timeout = "timeout";

    /// <summary>[Retry(2)]: the first attempt and two retries.</summary>
    public const int FailingAttempts = 3;

    public static int MaxExecutions(string kind) => string.Equals(kind, Failing, StringComparison.Ordinal) ? FailingAttempts : 1;
}

public sealed class EmptyJob : IJob;

public sealed class EmptyJobHandler : IJobHandler<EmptyJob>
{
    private readonly ExecutionRecorder _recorder;
    private readonly IJobContext _context;

    public EmptyJobHandler(ExecutionRecorder recorder, IJobContext context)
    {
        _recorder = recorder;
        _context = context;
    }

    public Task HandleAsync(EmptyJob message, CancellationToken cancellationToken)
    {
        _recorder.Begin(_context.JobId, ExecutionKinds.Empty);
        _recorder.End();

        return Task.CompletedTask;
    }
}

public sealed class DbJob : IJob
{
    public int Item { get; set; }
}

public sealed class DbJobHandler : IJobHandler<DbJob>
{
    private readonly ExecutionRecorder _recorder;
    private readonly IJobContext _context;
    private readonly BenchItemStore _store;

    public DbJobHandler(ExecutionRecorder recorder, IJobContext context, BenchItemStore store)
    {
        _recorder = recorder;
        _context = context;
        _store = store;
    }

    public async Task HandleAsync(DbJob message, CancellationToken cancellationToken)
    {
        _recorder.Begin(_context.JobId, ExecutionKinds.Db);
        try
        {
            await _store.IncrementAsync(message.Item, cancellationToken);
        }
        finally
        {
            _recorder.End();
        }
    }
}

public sealed class BenchMessage : IMessage
{
    public int Index { get; set; }
}

public sealed class BenchMessageHandlerA : IMessageHandler<BenchMessage>
{
    private readonly ExecutionRecorder _recorder;
    private readonly IJobContext _context;

    public BenchMessageHandlerA(ExecutionRecorder recorder, IJobContext context)
    {
        _recorder = recorder;
        _context = context;
    }

    public Task HandleAsync(BenchMessage message, CancellationToken cancellationToken)
    {
        _recorder.Begin(_context.JobId, ExecutionKinds.MessageA);
        _recorder.End();

        return Task.CompletedTask;
    }
}

public sealed class BenchMessageHandlerB : IMessageHandler<BenchMessage>
{
    private readonly ExecutionRecorder _recorder;
    private readonly IJobContext _context;

    public BenchMessageHandlerB(ExecutionRecorder recorder, IJobContext context)
    {
        _recorder = recorder;
        _context = context;
    }

    public Task HandleAsync(BenchMessage message, CancellationToken cancellationToken)
    {
        _recorder.Begin(_context.JobId, ExecutionKinds.MessageB);
        _recorder.End();

        return Task.CompletedTask;
    }
}

public sealed class BenchMessageHandlerC : IMessageHandler<BenchMessage>
{
    private readonly ExecutionRecorder _recorder;
    private readonly IJobContext _context;

    public BenchMessageHandlerC(ExecutionRecorder recorder, IJobContext context)
    {
        _recorder = recorder;
        _context = context;
    }

    public Task HandleAsync(BenchMessage message, CancellationToken cancellationToken)
    {
        _recorder.Begin(_context.JobId, ExecutionKinds.MessageC);
        _recorder.End();

        return Task.CompletedTask;
    }
}

public sealed class BatchChildJob : IJob
{
    public int Index { get; set; }
}

public sealed class BatchChildJobHandler : IJobHandler<BatchChildJob>
{
    private readonly ExecutionRecorder _recorder;
    private readonly IJobContext _context;

    public BatchChildJobHandler(ExecutionRecorder recorder, IJobContext context)
    {
        _recorder = recorder;
        _context = context;
    }

    public Task HandleAsync(BatchChildJob message, CancellationToken cancellationToken)
    {
        _recorder.Begin(_context.JobId, ExecutionKinds.BatchChild);
        _recorder.End();

        return Task.CompletedTask;
    }
}

public sealed class ContinuationParentJob : IJob;

public sealed class ContinuationParentJobHandler : IJobHandler<ContinuationParentJob>
{
    private readonly ExecutionRecorder _recorder;
    private readonly IJobContext _context;

    public ContinuationParentJobHandler(ExecutionRecorder recorder, IJobContext context)
    {
        _recorder = recorder;
        _context = context;
    }

    public Task HandleAsync(ContinuationParentJob message, CancellationToken cancellationToken)
    {
        _recorder.Begin(_context.JobId, ExecutionKinds.ContinuationParent);
        _recorder.End();

        return Task.CompletedTask;
    }
}

public sealed class ContinuationChildJob : IJob;

public sealed class ContinuationChildJobHandler : IJobHandler<ContinuationChildJob>
{
    private readonly ExecutionRecorder _recorder;
    private readonly IJobContext _context;

    public ContinuationChildJobHandler(ExecutionRecorder recorder, IJobContext context)
    {
        _recorder = recorder;
        _context = context;
    }

    public Task HandleAsync(ContinuationChildJob message, CancellationToken cancellationToken)
    {
        _recorder.Begin(_context.JobId, ExecutionKinds.ContinuationChild);
        _recorder.End();

        return Task.CompletedTask;
    }
}

// Two retries one and two seconds apart, so the failure path runs inside the measured window.
[Retry(2, Delays = new[] { 1, 2 })]
public sealed class FailingJob : IJob;

public sealed class FailingJobHandler : IJobHandler<FailingJob>
{
    private readonly ExecutionRecorder _recorder;
    private readonly IJobContext _context;

    public FailingJobHandler(ExecutionRecorder recorder, IJobContext context)
    {
        _recorder = recorder;
        _context = context;
    }

    public Task HandleAsync(FailingJob message, CancellationToken cancellationToken)
    {
        _recorder.Begin(_context.JobId, ExecutionKinds.Failing);
        _recorder.End();

        throw new InvalidOperationException("Intentional failure (matrix benchmark retry path).");
    }
}

public sealed class ScheduledJob : IJob;

public sealed class ScheduledJobHandler : IJobHandler<ScheduledJob>
{
    private readonly ExecutionRecorder _recorder;
    private readonly IJobContext _context;

    public ScheduledJobHandler(ExecutionRecorder recorder, IJobContext context)
    {
        _recorder = recorder;
        _context = context;
    }

    public Task HandleAsync(ScheduledJob message, CancellationToken cancellationToken)
    {
        _recorder.Begin(_context.JobId, ExecutionKinds.Scheduled);
        _recorder.End();

        return Task.CompletedTask;
    }
}

public sealed class RecurringBenchJob : IJob;

public sealed class RecurringBenchJobHandler : IJobHandler<RecurringBenchJob>
{
    private readonly ExecutionRecorder _recorder;
    private readonly IJobContext _context;

    public RecurringBenchJobHandler(ExecutionRecorder recorder, IJobContext context)
    {
        _recorder = recorder;
        _context = context;
    }

    public Task HandleAsync(RecurringBenchJob message, CancellationToken cancellationToken)
    {
        _recorder.Begin(_context.JobId, ExecutionKinds.Recurring);
        _recorder.End();

        return Task.CompletedTask;
    }
}

// Wait mode on all three gates, so every job runs exactly once and a refused one comes back later; Skip
// would delete them and leave nothing to check. The short handler delay keeps each gate genuinely busy.
[Mutex("matrix-mutex", Mode = ConcurrencyMode.Wait)]
public sealed class MutexJob : IJob;

public sealed class MutexJobHandler : IJobHandler<MutexJob>
{
    private readonly ExecutionRecorder _recorder;
    private readonly IJobContext _context;

    public MutexJobHandler(ExecutionRecorder recorder, IJobContext context)
    {
        _recorder = recorder;
        _context = context;
    }

    public async Task HandleAsync(MutexJob message, CancellationToken cancellationToken)
    {
        _recorder.Begin(_context.JobId, ExecutionKinds.Mutex);
        try
        {
            await Task.Delay(20, cancellationToken);
        }
        finally
        {
            _recorder.End();
        }
    }
}

[Semaphore("matrix-semaphore", 3)]
public sealed class SemaphoreJob : IJob;

public sealed class SemaphoreJobHandler : IJobHandler<SemaphoreJob>
{
    private readonly ExecutionRecorder _recorder;
    private readonly IJobContext _context;

    public SemaphoreJobHandler(ExecutionRecorder recorder, IJobContext context)
    {
        _recorder = recorder;
        _context = context;
    }

    public async Task HandleAsync(SemaphoreJob message, CancellationToken cancellationToken)
    {
        _recorder.Begin(_context.JobId, ExecutionKinds.Semaphore);
        try
        {
            await Task.Delay(20, cancellationToken);
        }
        finally
        {
            _recorder.End();
        }
    }
}

[RateLimit("matrix-rate", 5, 1, Mode = RateLimitMode.Wait)]
public sealed class RateLimitedJob : IJob;

public sealed class RateLimitedJobHandler : IJobHandler<RateLimitedJob>
{
    private readonly ExecutionRecorder _recorder;
    private readonly IJobContext _context;

    public RateLimitedJobHandler(ExecutionRecorder recorder, IJobContext context)
    {
        _recorder = recorder;
        _context = context;
    }

    public Task HandleAsync(RateLimitedJob message, CancellationToken cancellationToken)
    {
        _recorder.Begin(_context.JobId, ExecutionKinds.RateLimited);
        _recorder.End();

        return Task.CompletedTask;
    }
}

// Runs past its one-second budget, so the timeout cancels it and (Delete mode) the job ends Deleted.
[Timeout(1)]
public sealed class TimeoutJob : IJob;

public sealed class TimeoutJobHandler : IJobHandler<TimeoutJob>
{
    private readonly ExecutionRecorder _recorder;
    private readonly IJobContext _context;

    public TimeoutJobHandler(ExecutionRecorder recorder, IJobContext context)
    {
        _recorder = recorder;
        _context = context;
    }

    public async Task HandleAsync(TimeoutJob message, CancellationToken cancellationToken)
    {
        _recorder.Begin(_context.JobId, ExecutionKinds.Timeout);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
        finally
        {
            _recorder.End();
        }
    }
}
