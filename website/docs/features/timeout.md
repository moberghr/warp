---
sidebar_position: 5.5
---

# Job Timeout

Cap how long a job's handler is allowed to run. When the deadline expires, the worker cancels the handler's `CancellationToken`. The job either ends in `Deleted` (forget it) or surfaces as `Failed`/retried (treat it like a transient failure) — operator chooses per job.

Opt-in addon — register with `opt.AddTimeout()` on the builder.

## Quick start

```csharp
// Registration
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddRetry();        // optional — register BEFORE AddTimeout for Fail mode to retry
    opt.AddTimeout();
});

// On the job/request type — never the handler (default: Mode = Delete, Scope = PerAttempt)
[Timeout(seconds: 30)]
public class GenerateReport : IJob { }

// Per-publish extension (wins over the attribute)
await publisher.Enqueue(
    new GenerateReport(),
    new JobParameters().WithTimeout(TimeSpan.FromMinutes(5)));
```

## Modes

`TimeoutMode` controls what happens when the timer fires:

| Mode | End state | Retried by `AddRetry`? | Use when |
|---|---|---|---|
| `Delete` (default) | `Deleted`, `ExpireAt` set | No (outcome path bypasses retry's catch) | "Kill it and move on" — operator-style abandon. |
| `Fail` | `Failed` (or retried, if `AddRetry` is registered) | Yes (throws `TimeoutException`) | "Treat as a transient failure" — likely a slow upstream that may succeed on retry. |

```csharp
[Timeout(seconds: 30, Mode = TimeoutMode.Fail)]
public class CallSlowApi : IJob { }
```

## Scopes

`TimeoutScope` controls whether each retry gets its own fresh timeout or whether the deadline is anchored across the whole chain:

| Scope | Behaviour |
|---|---|
| `PerAttempt` (default) | Each attempt (initial + each retry) gets its own fresh `TimeoutSeconds` budget. Total wall-clock can be up to `(MaxRetries + 1) × TimeoutSeconds`. |
| `Total` | The publish behaviour stamps `DeadlineUtc = CreateTime + TimeoutSeconds` once. Each attempt computes `remaining = DeadlineUtc - now`. Past the deadline the timer fires immediately (zero-delay) and the configured `Mode` runs. Bounds total wall-clock to roughly `TimeoutSeconds` plus retry backoff. |

`Total` is only useful with `Mode = Fail` (otherwise there are no retries to bound). The deadline anchors at `CreateTime`, so queue-time burns into the budget — use `Total` for handlers that pick up quickly relative to their budget, or pair it with operational queue monitoring.

```csharp
// "Limit the total chain to 30s, retrying along the way."
[Timeout(seconds: 30, Mode = TimeoutMode.Fail, Scope = TimeoutScope.Total)]
public class CallPaymentApi : IJob { }
```

## Precedence

Most specific wins for both timeout duration and mode/scope:

```
WithTimeout(...)            // per-publish, highest priority
  → [Timeout(...)]          // on the job/request type
    → opt.AddTimeout(o =>   // global default, lowest priority
        o.Default = ...)
```

Set a fleet-wide safety net via the addon's options:

```csharp
opt.AddTimeout(o =>
{
    o.Default = TimeSpan.FromMinutes(10);
    o.DefaultMode = TimeoutMode.Delete;
    o.DefaultScope = TimeoutScope.PerAttempt;
});
```

Defaults to `Default = null` (no default — handlers without an attribute/extension are unrestricted).

## Pipeline ordering (Retry + Timeout)

`AddRetry()` MUST be called before `AddTimeout()` if both are registered. DI insertion order maps to pipeline outer → inner, so retry needs to wrap timeout for its `catch (Exception)` to see the `TimeoutException` thrown by `Fail` mode.

```csharp
opt.AddRetry();    // outer — sees TimeoutException, retries
opt.AddTimeout();  // inner — wraps the handler, throws on deadline
```

If you reverse the order, timed-out jobs in `Fail` mode end `Failed` after one attempt (retry never gets the exception).

## Cooperative cancellation only

Same rules as `DeleteJob`: the handler must honour its `CancellationToken`. If it ignores the token and runs to completion, the job ends `Completed` — the timeout doesn't fire after-the-fact. .NET removed `Thread.Abort` precisely because tearing down a thread mid-flight corrupts whatever it was touching; there is no safe in-process "hard kill". For truly unresponsive handlers the escape hatch is to recycle the worker process — `StaleJobRecovery` then re-enqueues any jobs whose `LastKeepAlive` aged out.

```csharp
public class GenerateReport : IJobHandler<GenerateReportRequest>
{
    public async Task HandleAsync(GenerateReportRequest req, CancellationToken ct)
    {
        foreach (var row in BigDataset())
        {
            ct.ThrowIfCancellationRequested();    // honour the token
            await Process(row, ct);               // pass it down
        }
    }
}
```

## What gets logged

Each timeout produces a job log entry with the `Timed out after Xs` message. In `Delete` mode it lands on the final `Deleted` row; in `Fail` mode it appears on the `Failed` row (`TimeoutException` message).

A dedicated `stats:timeout` counter is on the v1.1 roadmap — for now, operators query the audit log per-job.

## Out of scope (v1)

- **Hard kill** — see the cancellation note above.
- **Timeout on `IRequest<T>` / `IStreamRequest<T>`** — addon is `IJob`-only (`request is not IJob` bail-out). In-memory callers wrap their own `CancellationToken`.
- **Timeout on `IMessage` handlers** — same bail-out; handler-attribute placement is a planned design refactor (see roadmap). For now, put `[Timeout]` on `IJob` request types.
- **Dedicated "Timeouts" job-list tab** — defer until operators ask. The `Deleted`/`Failed` tabs already host timed-out jobs.
