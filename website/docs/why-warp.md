---
title: Why Warp
description: What Warp is, what it replaces, and when another tool is the better choice.
slug: /why-warp
---

# Why Warp

Warp is a .NET 10 library for background jobs, pub/sub messages and in-process requests. It stores its queue in the PostgreSQL or SQL Server database your application already uses, through your own EF Core `DbContext`.

## What you get

**One library for four kinds of work.** [Jobs](./patterns/jobs.md), [messages](./patterns/messages.md), [requests and streams](./patterns/requests.md) share one handler model and one pipeline. A logging or validation behavior written once applies to all of them.

```csharp
await publisher.Enqueue(new GenerateReport { Month = 3 });   // job: one handler, durable
await publisher.Publish(new OrderCreated { OrderId = id });  // message: every handler runs as its own job
var user = await mediator.Send(new GetUser { UserId = 1 });  // request: in-process, typed response
```

**Jobs commit with your data.** `Enqueue` and `Publish` write to your `DbContext`, so one `SaveChangesAsync` commits the order and the job that emails the customer, or neither. There is no broker to keep in step with the database. See the [outbox pattern](./features/outbox-pattern.md).

**A queue that is light on the database.** In dispatcher mode Warp 8.0 claims jobs in batches and writes completions in batches: about 0.22 statements per empty job, and a 20,000-job queue drained 16 times sooner than Hangfire with the same worker count on the same PostgreSQL server. See [Performance](./performance/index.md).

**Flow control on the job or the handler.** [`[Mutex]`](./features/mutex.md), [`[Semaphore]`](./features/semaphore.md), [`[RateLimit]`](./features/rate-limit.md), [`[Timeout]`](./features/timeout.md), [`[Retry]`](./patterns/jobs.md#retries) and [`[CircuitBreaker]`](./features/circuit-breaker.md) are attributes. The policy is resolved once and written on the job row, so a redeploy does not change a job that is already waiting.

**A dashboard that explains what happened.** Every job has its state history and its handler's `ILogger` output, live while it runs. Beyond jobs, the dashboard covers [error groups](./features/error-grouping.md), [queue wait and backlog](./features/queue-metrics.md), [SLOs](./features/slo-error-budget.md), [traces across jobs](./features/tracing.md), [outbound calls](./features/adapters.md) and [webhook deliveries](./features/webhooks.md). See the [dashboard overview](./dashboard/overview.md).

**More than a job runner.** [Sagas](./features/sagas.md), [recurring jobs](./features/recurring-jobs.md), [supervised background services](./features/background-services.md), [HTTP endpoints from handlers](./features/http.md) and [durable outbound webhooks](./features/webhooks.md) ship in the same packages.

## What it replaces

| If you use | Warp covers it with |
|---|---|
| Hangfire or Quartz.NET for background and recurring jobs | `IJob`, `IPublisher.Enqueue` / `Schedule`, recurring jobs. See [Migrating from Hangfire](./operations/migrating-from-hangfire.md). |
| MediatR for in-process requests | `IRequest<T>` and `IMediator.Send`, with source-generated dispatch |
| A hand-written outbox table and a poller | `IMessage` and `IPublisher.Publish` |
| Wolverine for messaging on a database | See [Migrating from Wolverine](./operations/migrating-from-wolverine.md). |
| A `BackgroundService` you cannot see into | [`WarpBackgroundService`](./features/background-services.md), with restart on fault and logs in the dashboard |

## When another tool fits better

- **You are not on EF Core with PostgreSQL or SQL Server.** Warp needs both. Hangfire supports many more storages, including Redis.
- **You are on an older .NET.** Warp targets .NET 10 only.
- **You need a broker between many services.** Warp's messages are delivered through one shared database. For RabbitMQ, Kafka or Azure Service Bus, use MassTransit or Wolverine.
- **You need workflows that replay deterministically over days.** Warp's sagas keep correlated state across messages; they are not a workflow engine like Temporal.
- **You need a long track record.** Hangfire has more than a decade in production and a large extension ecosystem. Warp is younger.

## Next

- [Getting Started](./getting-started.md): install, register, run the first job.
- [Demo shop](./demo-shop/index.md): a sample application that uses most features together.
