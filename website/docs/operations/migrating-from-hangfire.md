---
title: Migrating from Hangfire
description: How Hangfire's API maps onto Warp's, what changes in how you write a job, and how to move over gradually.
---

# Migrating from Hangfire

Hangfire and Warp both run background and recurring jobs on a database. The main difference in code is how a job is described: Hangfire stores a method call, and Warp stores a job object that a handler class receives.

For measured throughput and database load, see [Warp vs Hangfire](../performance/warp-vs-hangfire.md).

## The cheat sheet

| Hangfire | Warp |
|---|---|
| `AddHangfire(c => c.UsePostgreSqlStorage(...))` | `AddWarp<AppDbContext>(opt => opt.UsePostgreSql())` (publish only) |
| `AddHangfireServer(o => o.WorkerCount = 10)` | `AddWarpServer<AppDbContext>(opt => { opt.UsePostgreSql(); opt.WorkerCount = 10; })` |
| `BackgroundJob.Enqueue<T>(x => x.Send(id))` | `publisher.Enqueue(new SendEmail { Id = id })` then `SaveChangesAsync()` |
| `BackgroundJob.Schedule<T>(x => x.Send(id), delay)` | `publisher.Schedule(new SendEmail { Id = id }, DateTime.UtcNow.Add(delay))` |
| `BackgroundJob.ContinueJobWith(parentId, ...)` | `publisher.Enqueue(job, parentJobId)` |
| `RecurringJob.AddOrUpdate("id", ..., Cron.Hourly)` | `recurringPublisher.AddOrUpdateRecurringJob(job, name: "id", cron: "0 * * * *")` |
| `BatchJob.StartNew(...)` (Hangfire Pro) | `batchPublisher.StartNew(jobs)` and `ContinueBatchWith(...)` |
| `[AutomaticRetry(Attempts = 3)]` | `[Retry(3)]`, with `opt.AddRetry()` |
| `[DisableConcurrentExecution]` | `[Mutex("key")]`, with `opt.AddConcurrency()` |
| Throttling (Hangfire Ace) | `[Semaphore("key", limit: 5)]`, `[RateLimit]` |
| `[Queue("critical")]` | `publisher.Enqueue(job, queue: "critical")` |
| `IServerFilter` / `IClientFilter` | `IPipelineBehavior<,>` / `IPublishPipelineBehavior<>` |
| `PerformContext` and Hangfire.Console | Inject `ILogger<T>`; its output is stored with the job. `IJobContext` gives the job's id and metadata. |
| `BackgroundJob.Delete(id)` / `Requeue(id)` | `IJobCommandService.DeleteJob(id)` / `RequeueJob(id)` |
| `app.UseHangfireDashboard("/hangfire", options)` | `app.MapWarpDashboard("/warp").RequireAuthorization("Policy")` |

## A job is a class with a handler

Hangfire serializes a method call and its arguments:

```csharp
BackgroundJob.Enqueue<EmailService>(x => x.SendWelcome(userId));
```

Warp serializes a job object. A handler class receives it:

```csharp
public class SendWelcomeEmail : IJob
{
    public int UserId { get; set; }
}

public class SendWelcomeEmailHandler(EmailService emails) : IJobHandler<SendWelcomeEmail>
{
    public Task HandleAsync(SendWelcomeEmail job, CancellationToken ct) =>
        emails.SendWelcome(job.UserId, ct);
}
```

```csharp
await publisher.Enqueue(new SendWelcomeEmail { UserId = userId });
await publisher.SaveChangesAsync();
```

Handlers are found by a source generator at build time, so there is nothing to register. Renaming a method no longer breaks jobs that are already queued, because the stored payload is the job class, not a method signature.

## Enqueueing is part of your transaction

`BackgroundJob.Enqueue` writes to Hangfire's storage at once, on its own connection. If your own transaction then rolls back, the job still runs.

Warp's `Enqueue` adds the job to your `DbContext`. **Nothing is queued until you call `SaveChangesAsync`**, and the job commits or rolls back with the rest of that save. See the [outbox pattern](../features/outbox-pattern.md).

The same holds inside a handler: a handler that changes entities or enqueues more jobs must call `SaveChangesAsync` itself.

## Retries are opt-in

Hangfire retries a failed job 10 times by default. Warp does not retry unless you call `opt.AddRetry()`, which sets the global default. `[Retry]` on a job or handler overrides it. A failed job stays in the Failed list until someone requeues or deletes it; Warp never expires failed jobs on its own.

## The schema belongs to your migrations

Hangfire creates and upgrades its own tables at startup. Warp adds its entities to your EF Core model, in the `warp` schema by default, so they are created by your normal migrations:

```bash
dotnet ef migrations add AddWarp
dotnet ef database update
```

## Queues

Workers subscribe to queues with `opt.Queues`, and queues are drained in alphabetical order. Name them so the order is the priority you want, for example `a-critical`, `b-default`. For separate worker pools per queue, use [worker groups](./configuration.md#worker-groups).

## Moving over gradually

Warp and Hangfire can run in the same application and the same database, because Warp keeps its tables in its own schema.

1. Add Warp next to Hangfire and write new jobs as Warp jobs.
2. Move existing job types one at a time: add the job class and handler, switch the enqueue call, and let Hangfire finish what it already has queued for that type.
3. Move recurring jobs by registering them in Warp and removing them from Hangfire in the same deploy, so a schedule does not fire twice.
4. Remove Hangfire once its queues are empty.

Warp's [custom metrics](../features/custom-metrics.md#alongside-hangfire-or-jobly) and [webhooks](../features/webhooks.md) can also be used on their own while jobs stay on Hangfire.

## What has no equivalent

- **Enqueueing an arbitrary method call.** Every job needs a job class and a handler.
- **Storages other than PostgreSQL and SQL Server.** Warp has no Redis or in-memory production storage.
- **Older frameworks.** Warp targets .NET 10.
