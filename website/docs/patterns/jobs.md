---
sidebar_position: 2
---

# Jobs

Jobs implement `IJob` and have a **single handler**. They support scheduling, retries, continuations, batches, named queues, and mutex.

## Define a job

```csharp
public class GenerateReport : IJob
{
    public int ReportId { get; set; }
}

public class GenerateReportHandler : IJobHandler<GenerateReport>
{
    public async Task HandleAsync(GenerateReport message, CancellationToken ct)
    {
        // Generate the report
    }
}
```

## Enqueue

```csharp
await publisher.Enqueue(new GenerateReport { ReportId = 1 });
```

## Schedule

```csharp
await publisher.Schedule(new GenerateReport { ReportId = 1 }, DateTime.UtcNow.AddHours(1));
```

## Retries

Declare retry policy on the handler:

```csharp
[Retry(3)]
public class GenerateReportHandler : IJobHandler<GenerateReport>
{
    // ...
}
```

Or on the job class:

```csharp
[Retry(3, Delays = [15, 60, 300])]
public class GenerateReport : IJob
{
    public int ReportId { get; set; }
}
```

Override per-enqueue via metadata:

```csharp
await publisher.Enqueue(new GenerateReport { ReportId = 1 },
    new JobParameters().Configure<IRetryMetadata>(m => m.MaxRetries = 5));
```

Configure global defaults inside the `AddWarpServer` lambda:

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.AddRetry(o =>
    {
        o.MaxRetries = 3;
        o.Delays = [15, 60, 300]; // seconds
        o.JitterFactor = 0.2;     // ±20% random jitter on each delay (default: 0, no jitter)
    });
});
```

Priority: per-enqueue metadata > handler attribute > job attribute > global `RetryOptions`.

Failed jobs are retried automatically. Crash requeues (server died mid-execution) do **not** count against the retry limit.

`JitterFactor` is a multiplicative, global-only jitter applied to each computed delay: `delay * (1 + JitterFactor * rand(-1, 1))`. Clamped to `[0, 1]`. Useful when many jobs fail at the same time (e.g. downstream outage) to spread their retry attempts and avoid a thundering herd.

## Named Queues

```csharp
await publisher.Enqueue(new GenerateReport { ReportId = 1 }, queue: "critical");
```

Queues are processed in alphabetical order. A worker subscribes to specific queues:

```csharp
options.Queues = new[] { "a-critical", "b-default", "c-low" };
```

## Continuations

```csharp
var parentId = await publisher.Enqueue(new ProcessPayment { OrderId = 1 });
await publisher.Enqueue(new SendReceipt { OrderId = 1 }, parentId); // Runs after parent completes
```

## Batches

Batches use `IBatchPublisher` — a separate service from `IPublisher`. Inject it directly:

```csharp
public class MyService(IBatchPublisher batchPublisher) { }
```

```csharp
var jobs = orders.Select(o => new ProcessOrder { OrderId = o.Id }).ToList();
var batchId = await batchPublisher.StartNew(jobs);

// Continuation after batch completes
var followUps = new List<SendSummary> { new() };
await batchPublisher.ContinueBatchWith(followUps, batchId);
```

Batch continuation options control when continuations activate:

```csharp
// Default: continuation only fires when ALL jobs succeed
await batchPublisher.StartNew(jobs, ContinuationOptions.OnlyOnSucceeded);

// Fire when all jobs finish, regardless of success/failure
await batchPublisher.StartNew(jobs, ContinuationOptions.OnAnyFinishedState);
```

With `OnlyOnSucceeded` (default): if any job in the batch fails, the batch itself transitions to `Failed` and continuations stay in `Awaiting` state indefinitely. You can requeue the failed jobs from the dashboard — if they succeed on retry and the batch completes, continuations will activate normally.

With `OnAnyFinishedState`: continuations fire as soon as all jobs reach a terminal state (Completed or Failed), regardless of outcome. Use this when the continuation needs to run even if some jobs failed (e.g., sending a summary report).

## Recurring Jobs

Register a cron-based recurring job:

```csharp
await recurringPublisher.AddOrUpdateRecurringJob(
    new CleanupSessions(), name: "session-cleanup", cron: "0 * * * *");
```

This only registers the definition. The `RecurringJobScheduler` task creates jobs when the cron time arrives. See [Recurring Jobs](/docs/features/recurring-jobs) for full details.

## Cancellation

Cancel a running job gracefully:

```csharp
await jobCommandService.DeleteJob(jobId);
```

If the job is processing, this sets `CancellationMode = Graceful` instead of immediately changing state. The worker detects it and cancels the handler's `CancellationToken`. See [Job Cancellation](/docs/features/cancellation) for the full flow.

## Concurrency control (Mutex + Semaphore)

Cap the number of concurrent jobs per key. Requires `opt.AddConcurrency()` inside `AddWarpServer`. Use `[Mutex]` (`WithMutex`) for at-most-one, `[Semaphore]` (`WithSemaphore`) for at-most-N:

```csharp
// At most one CallPaymentApi for this customer at a time
await publisher.Enqueue(new ProcessPayment { CustomerId = 123 },
    new JobParameters().WithMutex("payment:123"));

// At most 5 concurrent calls to the payment API across all jobs
await publisher.Enqueue(new CallPaymentApi(),
    new JobParameters().WithSemaphore("payment-api", limit: 5));
```

Or use attributes on the job class for static keys:

```csharp
[Mutex("payment-processing")]
public class ProcessPayment : IJob { ... }

[Semaphore("payment-api", limit: 5)]
public class CallPaymentApi : IJob { ... }
```

See [Concurrency control](/docs/features/mutex) for details.
