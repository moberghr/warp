---
sidebar_position: 3
---

# Reliability

This page covers how Northwind Goods copes when its partners are slow, down, or strict about traffic, and
when its own workers crash. Every feature here is a policy on a job or handler, or a switch on the worker.
Each section links to the reference page for the full detail.

| Feature | Reference |
|---|---|
| [Retry](#retry) | [Jobs: retries](../patterns/jobs.md#retries), [Retrying view](../dashboard/retrying.md) |
| [Timeout](#timeout) | [Job timeout](../features/timeout.md) |
| [Circuit breaker](#circuit-breaker) | [Circuit breaker](../features/circuit-breaker.md) |
| [Rate limit](#rate-limit) | [Rate limit](../features/rate-limit.md) |
| [Semaphore](#semaphore) | [Semaphore](../features/semaphore.md) |
| [Mutex](#mutex) | [Concurrency control](../features/mutex.md) |
| [No-restart](#no-restart) | [NoRestart](../features/no-restart.md) |
| [Pause](#pause) | [Pause / resume](../features/pause.md) |
| [Batched completions](#batched-completions) | [Batched completions](../features/batched-completions.md) |

The policy addons run in `shop-worker`. Order matters: DI insertion order is outer to inner, so
`AddConcurrency()` comes before `AddRateLimit()`, `AddCircuitBreaker()` before `AddRetry()`, and `AddRetry()`
before `AddTimeout()`.

```csharp
// shop-worker
builder.Services.AddWarpServer<ShopDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddConcurrency();                  // before AddRateLimit
    opt.AddRateLimit();
    opt.AddCircuitBreaker();               // before AddRetry, so it counts settled failures only
    opt.AddRetry(o => o.MaxRetries = 3);   // before AddTimeout
    opt.AddTimeout();
    opt.AddNoRestart();
});

// shop-api: hosts the dashboard and enqueues jobs
builder.Services.AddWarp<ShopDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddConcurrency();   // the Concurrency limits page
    opt.AddRateLimit();     // the Rate limits page
    opt.AddNoRestart();     // stamps [NoRestart] on the jobs shop-api enqueues
});
```

`shop-api` registers three addons too. The dashboard runs there, and the **Concurrency limits** and **Rate
limits** pages read through services that only `AddConcurrency()` and `AddRateLimit()` register. Without them
the pages are hidden in `shop-api`, even though `shop-worker` enforces the limits. `AddNoRestart()` is needed
in every process that enqueues a `[NoRestart]` job (see [No-restart](#no-restart)).

## Retry

**In the shop.** Mailer returns an occasional 503. A confirmation email that fails once should be tried again
a little later, not left `Failed`. The shop sets a global default of three retries and gives
`SendConfirmationEmail` a longer budget of its own, because a late email is better than none.

**Wiring.**

```csharp
// Global default (above): opt.AddRetry(o => o.MaxRetries = 3);  delays default to 15s, 60s, 300s

[Retry(5, Delays = [30, 120, 600, 1800, 3600])]
public class SendConfirmationEmail : IMessageHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced message, CancellationToken ct) => /* call Mailer */ Task.CompletedTask;
}

// Per publish, for a job (wins over attributes and the global default)
await publisher.Enqueue(new BookShipment { OrderId = orderId }, new JobParameters().WithRetry(2));
```

**Test scenario.**

- *Given* `shop-worker` with the wiring above, and Mailer stubbed to fail the first two calls.
- *When* you place an order.
- *Then* the `SendConfirmationEmail` job appears under **Retrying** on the dashboard Jobs page, sitting in
  `Scheduled`, with **Attempt** `#2 (1 failed)` and **Next attempt** about 30 s out, then `#3 (2 failed)` about
  2 minutes later. The third attempt succeeds and the job ends `Completed`; its metadata shows
  `RetriedTimes: 2`. The other two `OrderPlaced` children are unaffected. Make Mailer fail every call and the
  job ends `Failed` after the sixth attempt.
- `Covered by:` `RetryIntegrationTests` (`GivenFailingJobWithThreeRetries_WhenProcessed_ThenRetriesThreeTimesThenFails`,
  `GivenHandlerRetryAttribute_PublishedWithoutMetadata_RetriesPastGlobalDefault`), `RetryTests`,
  `MessagePolicyTests` (`HandlerDeclaredRetry_MessageChildRetriesPerChild_SiblingUnaffected`).

Pitfall: a retry policy is atomic per rung. `WithRetry(2)` at publish takes the global delays, not the
attribute's. Crash requeues do not count against the budget.

## Timeout

**In the shop.** The ShipIt booking API sometimes hangs for minutes. A `BookShipment` attempt that has not
finished in 30 seconds is treated as a transient failure and retried, so the order is not stuck behind a dead
connection.

**Wiring.**

```csharp
[Timeout(seconds: 30, Mode = TimeoutMode.Fail)]
public class BookShipment : IJob
{
    public Guid OrderId { get; set; }
}

public class BookShipmentHandler(IShipItClient shipIt) : IJobHandler<BookShipment>
{
    public Task HandleAsync(BookShipment job, CancellationToken ct) =>
        shipIt.BookAsync(job.OrderId, ct);   // pass ct down, or the timeout cannot stop the call
}
```

**Test scenario.**

- *Given* the ShipIt sandbox is set to delay its booking response by 60 seconds.
- *When* an order is paid and `BookShipment` is enqueued.
- *Then* after 30 s the attempt fails with `TimeoutException: Timed out after 30s` in the job's log, and the
  job moves to **Retrying**. Each retry gets a fresh 30 s budget (`PerAttempt`).
  After the retries are spent the job ends `Failed`. Change `Mode` to the default `Delete` and the first
  timeout ends the job `Deleted` with no retry.
- `Covered by:` `TimeoutIntegrationTests` (`FailMode_PlusAddRetry_PerAttempt_IsRetriedThenFails`,
  `DeleteMode_PlusAddRetry_TimedOutNotRetried`), `TimeoutAddonOrderingTests`.

Pitfall: register `AddRetry()` before `AddTimeout()`. In the reverse order a `Fail`-mode timeout ends
`Failed` after one attempt, because Retry never sees the `TimeoutException`.

## Circuit breaker

**In the shop.** Stockroom goes down for minutes at a time. Without a breaker every `ReserveStock` job keeps
calling it, burning retries and piling load on a system that is trying to recover. With a breaker, after five
failed reservations the circuit opens and further reservations are rescheduled past the outage instead of
running.

**Wiring.**

```csharp
// opt.AddCircuitBreaker(o => { o.Threshold = 5; o.Duration = TimeSpan.FromMinutes(2); });

[CircuitBreaker(Group = "stockroom")]
public class ReserveStock(IPublisher publisher) : IMessageHandler<OrderPlaced>
{
    public async Task HandleAsync(OrderPlaced message, CancellationToken ct)
    {
        // call Stockroom, then:
        await publisher.Publish(new StockReserved { OrderId = message.OrderId });
    }
}
```

**Test scenario.**

- *Given* `shop-worker` runs with `opt.AddRetry(o => o.MaxRetries = 0)` for this test, `Threshold = 5`, and
  Stockroom is stopped.
- *When* you place six orders.
- *Then* the first five `ReserveStock` jobs end `Failed`. The sixth stays in **Scheduled** with a future
  `ScheduleTime`, and its **History** shows `Rescheduled due to circuit breaker 'stockroom' (open)`.
  Start Stockroom again. After `Duration` one job runs as the probe (other jobs log `(probe-in-progress)`),
  succeeds, the circuit closes, and the remaining reservations complete.
- `Covered by:` `CircuitBreakerIntegrationTests`
  (`GivenFailingHandler_WhenThresholdHit_ThenCircuitOpensAndSubsequentJobsRescheduled`),
  `CircuitBreakerRetryOrderingTests`, `CircuitBreakerTests`.

Pitfall: with `AddCircuitBreaker()` before `AddRetry()`, intermediate retry attempts do not count as breaker
failures; only an exhausted retry budget does. With the default delays a real outage takes several minutes of
retries before the circuit opens. Register them the other way round and the breaker sits inside retry, sees
every failed attempt, and opens after five attempts instead of five failed jobs.

## Rate limit

**In the shop.** ShipIt allows 60 label requests per minute per account and rejects the rest. On a busy
morning fulfilment prints hundreds of labels. `PrintShippingLabel` is rate limited in `Wait` mode, so surplus
labels are deferred to the next window instead of being sent and rejected.

**Wiring.**

```csharp
[RateLimit("shipit-labels", count: 60, perSeconds: 60, Mode = RateLimitMode.Wait)]
public class PrintShippingLabel : IJob
{
    public Guid OrderId { get; set; }
}
```

**Test scenario.**

- *Given* `shop-api` registers `AddRateLimit()` (see the registration above), and an operator sets an
  override of 2 per 60 s for key `shipit-labels` on the dashboard **Rate limits** page (`/warp/ratelimits`).
- *When* you pay for three orders within a few seconds, so three `PrintShippingLabel` jobs run.
- *Then* two labels print. The third job's **History** shows
  `Throttled — rate limit 'shipit-labels' (2/60s), rescheduled to <time>`, it sits in `Scheduled`, and it
  completes in the next window. Remove the override and the attribute's 60 per minute applies again. With
  the default `Skip` mode the third job would end `Deleted` with `Cancelled — rate limit ... exceeded`.
- `Covered by:` `RateLimitIntegrationTests` (`GivenJobAtLimit_WhenWaitMode_ThenOneThrottledOneCompletes`,
  `GivenThreeJobsAtFixedLimitOfTwo_WhenSkipMode_ThenExactlyOneIsCancelled`), `RateLimitCompositionTests`,
  `RateLimitManagerTests`.

Pitfall: the default mode is `Skip`, which drops work. Customer-visible jobs need `Wait`. `Wait` reschedules
ride `ScheduledActivationInterval` (10 s by default); DB push does not speed them up.

## Semaphore

**In the shop.** The shop's Mailer plan allows five concurrent connections. During a sale, dozens of
confirmation emails are ready at once. A semaphore caps `SendConfirmationEmail` at five running jobs across
all `shop-worker` instances. Surplus jobs wait in the queue; none are dropped.

**Wiring.**

```csharp
[Semaphore("mailer", limit: 5)]   // default mode is Wait
[Retry(5, Delays = [30, 120, 600, 1800, 3600])]
public class SendConfirmationEmail : IMessageHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced message, CancellationToken ct) => /* call Mailer */ Task.CompletedTask;
}
```

**Test scenario.**

- *Given* `shop-api` registers `AddConcurrency()`, an operator lowers the `mailer` limit to 1 on the
  **Concurrency limits** page (`/warp/concurrency`), and `SendConfirmationEmail` waits on a breakpoint.
- *When* you place two orders.
- *Then* one `SendConfirmationEmail` is `Processing`. The other's **History** shows
  `Requeued — 'mailer' full (1 slots)` and it returns to `Enqueued` until the first finishes, then completes.
  Remove the admin row and five run at once again.
- `Covered by:` `SemaphoreIntegrationTests`
  (`GivenSemaphoreLimit2_WhenThreeJobsEnqueued_ThenTwoRunConcurrentlyAndThirdWaits`), `SemaphoreTests`,
  `ConcurrencyLimitManagerTests`.

Pitfall: admin rows survive redeploys. A limit raised during an incident stays until someone removes it.

## Mutex

**In the shop.** The payment-result callback is cheap and handled inline (see
[Inbound HTTP endpoints](./integrations.md#inbound-http-endpoints)). Refund and chargeback notifications are
slower: each one restocks items in Stockroom and adjusts the order. PayFast can send two of them for one
order almost at once (a partial refund, then a chargeback). If both run in parallel, both read the same order
totals and the second overwrites the first. `shop-api` enqueues each notification as a
`ProcessPaymentAdjustment` job with a mutex keyed on the order id, in `Wait` mode, so adjustments for one
order run one at a time while different orders run in parallel.

**Wiring.**

```csharp
public class ProcessPaymentAdjustment : IJob
{
    public Guid OrderId { get; set; }
    public string PayFastEventId { get; set; } = "";
}

// shop-api, PayFast refund/chargeback endpoint
await publisher.Enqueue(
    new ProcessPaymentAdjustment { OrderId = orderId, PayFastEventId = eventId },
    new JobParameters().WithMutex($"order:{orderId}", ConcurrencyMode.Wait));
await publisher.SaveChangesAsync(ct);

// shop-worker: the handler restocks in Stockroom and updates the order totals
```

**Test scenario.**

- *Given* `ProcessPaymentAdjustmentHandler` waits on a breakpoint.
- *When* you replay two PayFast sandbox refund notifications for the same order, then one for a different order.
- *Then* the first adjustment is `Processing`. The second shows `Requeued — 'order:<id>' full (1 slots)` in its
  **History** and runs only after the first completes. The adjustment for the other order runs at the same
  time as the first. The concurrency key is visible in each job's metadata.
- `Covered by:` `MutexIntegrationTests` (`GivenTwoJobsWithSameMutex_WhenWaitMode_ThenSecondRequeuesUntilFirstFinishes`,
  `GivenTwoJobsWithDifferentKeys_WhenWaitMode_BothEnterHandlerSimultaneously`), `MutexTests`,
  `ConcurrencyIntegrationTests`.

Pitfalls: `[Mutex("...")]` takes a literal key; it does not substitute `{id}`. A per-order key must come from
`WithMutex` at enqueue. The default mode is `Skip`, which would drop the second adjustment.

## No-restart

**In the shop.** Capturing a PayFast payment must never run twice. If `shop-worker` dies in the middle of
`CapturePayment`, the shop does not know whether PayFast charged the card. Normally crash recovery requeues
the job; with `[NoRestart]` it is marked `Failed` instead, and support checks PayFast before acting.

**Wiring.**

```csharp
// opt.AddNoRestart();  (in the registration above)

[NoRestart]
public class CapturePayment : IJob
{
    public Guid OrderId { get; set; }
}
```

**Test scenario.**

- *Given* `CapturePaymentHandler` waits on a breakpoint and a second `shop-worker` instance is running.
- *When* the job is `Processing` on the first instance, kill that process (no graceful shutdown).
- *Then* once `LastKeepAlive` is older than `InvisibilityTimeout` (5 minutes by default) and the next
  `StaleJobRecovery` tick runs (every 30 s), the job is `Failed` with the log
  `Failed by crash recovery — job opted out of restart`. The server task log shows
  `Recovered 1 stale jobs (0 requeued, 1 failed, 0 deleted)`. Repeat with `BookShipment`, which has no
  attribute: it is requeued and runs on the surviving instance.
- `Covered by:` `NoRestartIntegrationTests` (`GivenNoRestartAttributeJob_WhenPublishedAndStale_ThenMarkedFailed`),
  `NoRestartCrashRecoveryTests`, `NoRestartAddonTests`, `CrashRecoveryTests`.

Pitfall: the attribute is read when the job is enqueued, so `opt.AddNoRestart()` must run in every process
that enqueues `CapturePayment`, not only in `shop-worker`. A process without it enqueues the job with no
restart flag, and the attribute is silently ignored. `WithRestart(false)` at publish works without the addon.

## Pause

**In the shop.** Stockroom has a maintenance window every Sunday night. Stockroom jobs run on their own
`stockroom` queue and worker group. The operator pauses that group for the window; payment and email jobs
keep running, and Stockroom jobs wait in `Enqueued` until the group is resumed.

**Wiring.**

```csharp
// shop-worker
builder.Services.AddWarpServer<ShopDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddWorkerGroup(g =>
    {
        g.Queues = ["stockroom"];
        g.WorkerCount = 2;
    });
});

// Pause / resume from the server detail page, or:
// POST {prefix}/api/groups/{groupId}/pause
// POST {prefix}/api/groups/{groupId}/resume
```

**Test scenario.**

- *Given* `shop-worker` with the `stockroom` worker group.
- *When* you pause that group on the dashboard, wait one heartbeat (`HealthCheckInterval`, 5 s by default),
  then enqueue a Stockroom job and place an order.
- *Then* the Stockroom job stays `Enqueued`, while the order's payment and email jobs complete on the
  default group. A job that was already `Processing` when you paused runs to completion. Resume the group and
  the waiting job is picked up within about 15 s: one heartbeat (5 s) for the workers to see the resume,
  plus one `PollingInterval` (10 s) before they fetch again.
- `Covered by:` `PauseIntegrationTests` (`PauseWorkerGroup_JobsStayEnqueued`,
  `PauseWorkerGroup_Resume_JobsGetProcessed`), `PauseStateHolderTests`.

Pitfall: pause is not instant. It takes effect at the next heartbeat and never cancels running handlers.

## Batched completions

**In the shop.** ShipIt sends a tracking update for every parcel scan, and each becomes a short
`ApplyTrackingUpdate` job. Thousands of 20 ms jobs an hour means the completion write is a large share of the
work. Dispatcher mode buffers completions and writes them in one transaction per batch.

**Wiring.**

```csharp
// shop-worker
builder.Services.AddWarpServer<ShopDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.UseDispatcher = true;
    opt.CompletionBatchSize = 50;                                  // default
    opt.CompletionFlushInterval = TimeSpan.FromMilliseconds(100);  // default
});
```

**Test scenario.**

- *Given* `shop-worker` in dispatcher mode with `CompletionFlushInterval` raised to 5 s for the test, and
  `ApplyTrackingUpdate` handlers that take about 50 ms.
- *When* you enqueue 2,000 `ApplyTrackingUpdate` jobs at once, so every worker stays busy.
- *Then* all 2,000 end `Completed`. While the backlog drains, finished jobs flip to `Completed` in groups (up
  to 50 per worker, or every 5 s) rather than one by one, so a job can show `Processing` for a few seconds
  after its handler output says it finished. A lone job does not show this delay: a worker flushes its buffer
  as soon as it has nothing else to run. Stop `shop-worker` gracefully while the backlog drains: the buffered
  completions are flushed on shutdown and those jobs are `Completed`, not left `Processing`.
- `Covered by:` `BatchedCompletionIntegrationTests` (`GivenManyShortJobs_WhenProcessed_ThenAllReachCompletedState`,
  `GivenShutdown_WhenServerStops_ThenPendingCompletionsAreFlushed`), `CompletionBatchTests`.

Pitfall: a crash between handler completion and the flush leaves the job `Processing`, and crash recovery may
run it again. Use it only for idempotent handlers or ones marked `[NoRestart]`.
