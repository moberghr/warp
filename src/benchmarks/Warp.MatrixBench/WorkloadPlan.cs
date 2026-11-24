using Warp.Core.Enums;

namespace Warp.MatrixBench;

/// <summary>
/// What a cell publishes and therefore what it must find afterwards: job rows per type and final state,
/// handler executions per kind, and the per-job denominator.
/// </summary>
public sealed record WorkloadPlan
{
    public const int BatchSize = 4;

    public const int MessageHandlers = 3;

    public required int PlainJobs { get; init; }

    public required int Specials { get; init; }

    public required int DbJobs { get; init; }

    /// <summary>Job rows (Kind = Job) the measured insert produces, routed message children included.</summary>
    public required int MeasuredJobs { get; init; }

    /// <summary>Distinct job ids each handler kind must have run, each at most <see cref="ExecutionKinds.MaxExecutions"/> times.</summary>
    public required Dictionary<string, int> ExecutionsByKind { get; init; }

    /// <summary>Final (state, count) of Kind = Job rows per job type, warm-up included.</summary>
    public required Dictionary<string, (State State, int Count)> JobRows { get; init; }

    public required int Messages { get; init; }

    public required int Batches { get; init; }

    public int ExpectedExecutions => ExecutionsByKind.Sum(x => x.Value * ExecutionKinds.MaxExecutions(x.Key));

    public static WorkloadPlan For(CellOptions options)
    {
        if (string.Equals(options.JobType, Workloads.Db, StringComparison.Ordinal))
        {
            return new WorkloadPlan
            {
                PlainJobs = options.Jobs,
                Specials = 0,
                DbJobs = options.Jobs,
                MeasuredJobs = options.Jobs,
                ExecutionsByKind = new(StringComparer.Ordinal) { [ExecutionKinds.Db] = options.Jobs },
                JobRows = new(StringComparer.Ordinal) { [nameof(DbJob)] = (State.Completed, options.Jobs + options.WarmupJobs) },
                Messages = 0,
                Batches = 0,
            };
        }

        if (!string.Equals(options.JobType, Workloads.Mixed, StringComparison.Ordinal))
        {
            return new WorkloadPlan
            {
                PlainJobs = options.Jobs,
                Specials = 0,
                DbJobs = 0,
                MeasuredJobs = options.Jobs,
                ExecutionsByKind = new(StringComparer.Ordinal) { [ExecutionKinds.Empty] = options.Jobs },
                JobRows = new(StringComparer.Ordinal) { [nameof(EmptyJob)] = (State.Completed, options.Jobs + options.WarmupJobs) },
                Messages = 0,
                Batches = 0,
            };
        }

        // Per special index: 3 routed message children, 4 batch children, a continuation pair, and one each
        // of failing, scheduled, mutex, semaphore, rate-limited and timeout jobs.
        const int JobRowsPerSpecial = MessageHandlers + BatchSize + 2 + 6;
        var specials = options.SpecialsPerFeature;
        var plain = options.Jobs - (specials * JobRowsPerSpecial);

        return new WorkloadPlan
        {
            PlainJobs = plain,
            Specials = specials,
            DbJobs = 0,
            MeasuredJobs = plain + (specials * JobRowsPerSpecial),
            ExecutionsByKind = new(StringComparer.Ordinal)
            {
                [ExecutionKinds.Empty] = plain,
                [ExecutionKinds.MessageA] = specials,
                [ExecutionKinds.MessageB] = specials,
                [ExecutionKinds.MessageC] = specials,
                [ExecutionKinds.BatchChild] = specials * BatchSize,
                [ExecutionKinds.ContinuationParent] = specials,
                [ExecutionKinds.ContinuationChild] = specials,
                [ExecutionKinds.Failing] = specials,
                [ExecutionKinds.Scheduled] = specials,
                [ExecutionKinds.Mutex] = specials,
                [ExecutionKinds.Semaphore] = specials,
                [ExecutionKinds.RateLimited] = specials,
                [ExecutionKinds.Timeout] = specials,
            },
            JobRows = new(StringComparer.Ordinal)
            {
                [nameof(EmptyJob)] = (State.Completed, plain + options.WarmupJobs),
                [nameof(BenchMessage)] = (State.Completed, specials * MessageHandlers),
                [nameof(BatchChildJob)] = (State.Completed, specials * BatchSize),
                [nameof(ContinuationParentJob)] = (State.Completed, specials),
                [nameof(ContinuationChildJob)] = (State.Completed, specials),
                [nameof(FailingJob)] = (State.Failed, specials),
                [nameof(ScheduledJob)] = (State.Completed, specials),
                [nameof(MutexJob)] = (State.Completed, specials),
                [nameof(SemaphoreJob)] = (State.Completed, specials),
                [nameof(RateLimitedJob)] = (State.Completed, specials),
                [nameof(TimeoutJob)] = (State.Deleted, specials),
            },
            Messages = specials,
            Batches = specials,
        };
    }
}
