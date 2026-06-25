using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Helper;

namespace Warp.Core.Testing;

/// <summary>Which publish API produced a <see cref="PublishedJob"/>.</summary>
public enum PublishedJobKind
{
    Job = 1,
    Message = 2,
}

/// <summary>Which batch API produced a <see cref="PublishedBatch"/>.</summary>
public enum PublishedBatchKind
{
    StartNew = 1,
    Continuation = 2,
}

/// <summary>A record of one <see cref="IBatchPublisher.StartNew{T}"/> or
/// <see cref="IBatchPublisher.ContinueBatchWith{T}"/> call captured by <see cref="InMemoryPublisher"/>.</summary>
public sealed record PublishedBatch
{
    public required Guid Id { get; init; }

    public required PublishedBatchKind Kind { get; init; }

    /// <summary>The child <c>IJob</c> instances in the batch.</summary>
    public required IReadOnlyList<object> Children { get; init; }

    public string? Name { get; init; }

    public Guid? ParentId { get; init; }

    public ContinuationOptions Options { get; init; }

    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}

/// <summary>A record of one <see cref="IPublisher.Enqueue{T}(T)"/>, <c>Schedule</c>, or
/// <see cref="IPublisher.Publish{T}(T)"/> call captured by <see cref="InMemoryPublisher"/>.</summary>
public sealed record PublishedJob
{
    public required Guid Id { get; init; }

    /// <summary>The <c>IJob</c> or <c>IMessage</c> instance that was published.</summary>
    public required object Payload { get; init; }

    public required PublishedJobKind Kind { get; init; }

    public string? Queue { get; init; }

    public DateTime? ScheduleTime { get; init; }

    public Guid? ParentJobId { get; init; }

    public JobParameters? Parameters { get; init; }
}

/// <summary>
/// A drop-in <see cref="IPublisher"/> for unit/integration tests that records every publish in
/// memory instead of writing to a Warp store. Lets you test handlers and application code that
/// call <c>IPublisher.Enqueue</c> / <c>Publish</c> / <c>Schedule</c> without standing up the full
/// Warp database (no <c>Job</c> DbSet required). Register it in place of the real publisher:
/// <code>services.AddSingleton&lt;IPublisher&gt;(new InMemoryPublisher());</code>
/// then assert against <see cref="Published"/> and <see cref="SaveChangesCount"/>.
/// Also implements <see cref="IBatchPublisher"/> — batches are recorded in <see cref="Batches"/>.
/// </summary>
public sealed class InMemoryPublisher : IPublisher, IBatchPublisher
{
    private readonly List<PublishedJob> _published = [];
    private readonly List<PublishedBatch> _batches = [];

    /// <summary>Every job/message published, in call order. Recorded eagerly on each publish —
    /// independent of whether <see cref="SaveChangesAsync"/> was called.</summary>
    public IReadOnlyList<PublishedJob> Published => _published;

    /// <summary>Every batch created via <see cref="IBatchPublisher"/>, in call order.</summary>
    public IReadOnlyList<PublishedBatch> Batches => _batches;

    /// <summary>How many times <see cref="SaveChangesAsync"/> has been called.</summary>
    public int SaveChangesCount { get; private set; }

    /// <summary>Drops all recorded publishes/batches and resets <see cref="SaveChangesCount"/>.</summary>
    public void Clear()
    {
        _published.Clear();
        _batches.Clear();
        SaveChangesCount = 0;
    }

    public Task<Guid> Publish<T>(T message)
        where T : class, IMessage
        => Task.FromResult(Record(message, PublishedJobKind.Message, queue: null, scheduleTime: null, parentJobId: null));

    public Task<Guid> Publish<T>(T message, string? queue)
        where T : class, IMessage
        => Task.FromResult(Record(message, PublishedJobKind.Message, queue, scheduleTime: null, parentJobId: null));

    public Task<Guid> Enqueue<T>(T job)
        where T : class, IJob
        => Task.FromResult(Record(job, PublishedJobKind.Job, queue: null, scheduleTime: null, parentJobId: null));

    public Task<Guid> Enqueue<T>(T job, string? queue)
        where T : class, IJob
        => Task.FromResult(Record(job, PublishedJobKind.Job, queue, scheduleTime: null, parentJobId: null));

    public Task<Guid> Enqueue<T>(T job, Guid parentJobId)
        where T : class, IJob
        => Task.FromResult(Record(job, PublishedJobKind.Job, queue: null, scheduleTime: null, parentJobId));

    public Task<Guid> Enqueue<T>(T job, Guid parentJobId, string? queue)
        where T : class, IJob
        => Task.FromResult(Record(job, PublishedJobKind.Job, queue, scheduleTime: null, parentJobId));

    public Task<Guid> Enqueue<T>(T job, JobParameters jobParameters)
        where T : class, IJob
        => Task.FromResult(Record(job, PublishedJobKind.Job, jobParameters.Queue, jobParameters.ScheduleTime, jobParameters.ParentId, jobParameters));

    public Task<Guid> Schedule<T>(T job, DateTime scheduleTime)
        where T : class, IJob
        => Task.FromResult(Record(job, PublishedJobKind.Job, queue: null, scheduleTime, parentJobId: null));

    public Task<Guid> Schedule<T>(T job, DateTime scheduleTime, string? queue)
        where T : class, IJob
        => Task.FromResult(Record(job, PublishedJobKind.Job, queue, scheduleTime, parentJobId: null));

    public Task<Guid> Schedule<T>(T job, DateTime scheduleTime, Guid parentJobId)
        where T : class, IJob
        => Task.FromResult(Record(job, PublishedJobKind.Job, queue: null, scheduleTime, parentJobId));

    public Task<Guid> Schedule<T>(T job, DateTime scheduleTime, Guid parentJobId, string? queue)
        where T : class, IJob
        => Task.FromResult(Record(job, PublishedJobKind.Job, queue, scheduleTime, parentJobId));

    public Task<Guid> StartNew<T>(List<T> batchJobMessages, string? name = null, ContinuationOptions options = ContinuationOptions.OnlyOnSucceeded, Dictionary<string, object>? metadata = null)
        where T : class, IJob
        => Task.FromResult(RecordBatch(batchJobMessages, PublishedBatchKind.StartNew, parentId: null, name, options, metadata));

    public Task<Guid> ContinueBatchWith<T>(List<T> batchJobMessages, Guid parentId, string? name = null, ContinuationOptions options = ContinuationOptions.OnlyOnSucceeded, Dictionary<string, object>? metadata = null)
        where T : class, IJob
        => Task.FromResult(RecordBatch(batchJobMessages, PublishedBatchKind.Continuation, parentId, name, options, metadata));

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveChangesCount++;

        return Task.CompletedTask;
    }

    private Guid RecordBatch<T>(List<T> children, PublishedBatchKind kind, Guid? parentId, string? name, ContinuationOptions options, Dictionary<string, object>? metadata)
        where T : class, IJob
    {
        if (children == null || children.Count == 0)
        {
            throw new ArgumentException("List cannot be empty", nameof(children));
        }

        var id = Guid.NewGuid();
        _batches.Add(new PublishedBatch
        {
            Id = id,
            Kind = kind,
            Children = [.. children],
            Name = name,
            ParentId = parentId,
            Options = options,
            Metadata = metadata,
        });

        return id;
    }

    private Guid Record(object payload, PublishedJobKind kind, string? queue, DateTime? scheduleTime, Guid? parentJobId, JobParameters? parameters = null)
    {
        var id = Guid.NewGuid();
        _published.Add(new PublishedJob
        {
            Id = id,
            Payload = payload,
            Kind = kind,
            Queue = queue,
            ScheduleTime = scheduleTime,
            ParentJobId = parentJobId,
            Parameters = parameters,
        });

        return id;
    }
}
