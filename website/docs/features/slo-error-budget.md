# SLO / error budget

Warp already folds every health signal it needs into durable aggregates — per-type/handler execution, queue-wait, backlog depth, deadline attainment. **SLOs** turn those into promises: declare an objective (a target over a rolling window), and Warp continuously computes **attainment**, the remaining **error budget**, and a multi-window **burn rate**, surfaces them on the dashboard, and fires an operational event on a burn — all off the worker hot path, reading the metrics that are already there.

It's an opt-in feature: call `AddSlo(...)` to turn evaluation on. The objective/evaluation tables and the read/command services are always in the schema, so a dashboard-only process can still view and edit objectives.

## Objective kinds

| Kind | Measures | Target | Windowed |
|---|---|---|---|
| `SuccessRate` | succeeded ÷ (succeeded + failed), per job type | ratio (e.g. `0.995`) | ✓ |
| `ExecutionLatency` | handler execution percentile, per job type | ms | ✓ (via `pcth`) |
| `QueueWaitLatency` | queue-wait percentile, per queue | ms | ✓ (via `pcth`) |
| `BacklogDepth` | current backlog depth, per queue | job count | current gauge |
| `DeadlineAttainment` | fraction of `Total`-scope jobs that met their deadline (§8.7) | ratio | ✓ |

Rate/attainment kinds use the standard error-budget model (`budget = 1 − burn`, where `burn = observed error rate ÷ allowed error rate`); latency/depth kinds compare the observed value to the target (lower is better). A retried-then-succeeded job counts as a success. For `DeadlineAttainment`, a job that reaches a terminal state *after* its Total-scope deadline counts as a miss — including a late `Completed`, since the deadline is a time bound, not just a failure signal.

Latency objectives read a bucketed histogram, and the job-domain ladder (execution + queue-wait) extends to **5 minutes** — job timescales run well past the 10 s top of the HTTP-scale surfaces — so a "p95 < 30s" objective observes and breaches at real job latencies rather than saturating at 10 s.

## Fast-burn

The **short (fast-burn) window** is the objective's window ÷ 12, floored to 5 minutes — and it's real, because it reads the **5-minute fine tier** from the metrics retention tiers (§8.30). Every objective reports two burn rates: **fast** (recent) and **slow** (full window), so a sudden burn is caught quickly while the slow window governs the sustained budget. `ExecutionLatency`/`QueueWaitLatency` read the tiered `pcth` latency histogram, so their percentiles are windowed rather than all-time.

Everything is **off the hot path** — a periodic `SloEvaluator` server task reads the already-folded aggregates and upserts one status row per objective. The only new write on the hot path is the deadline-miss counter (§8.7), recorded once at finalization alongside the execution counters.

## Configuration

Seed objectives in code, edit them in the dashboard — the DB row wins, so a dashboard edit is never clobbered on restart:

```csharp
services.AddWarpServer<AppDb>(opt =>
{
    opt.UsePostgreSql();

    opt.AddSlo(o =>
    {
        o.AddObjective(SloKind.SuccessRate, "MyApp.Jobs.SendEmail", target: 0.995);
        o.AddObjective(SloKind.QueueWaitLatency, "default", target: 30_000, percentile: 95);
        o.AddObjective(SloKind.DeadlineAttainment, "MyApp.Jobs.Charge", target: 0.99);
    });

    opt.SloEvaluationInterval = TimeSpan.FromMinutes(1); // default; null disables evaluation
});
```

## Alerting

On a healthy→breaching edge the evaluator fires a `WarpEventType.SloBreached` operational event through the notifier seam (`AddNotifier<T>`, §8.25) — `BacklogDepth` objectives fire the previously-reserved `BacklogBreached`. Severity is `Error` on a fast burn, `Warning` on a slow one. It fires **once per edge**, not every tick, and is suppressed while an operator has acknowledged the objective. Delivery is the host's notifier (Slack, email, PagerDuty, …) — Warp raises the event, the host routes it.

## Dashboard

The **SLOs** page (`/slo`, gated on the `slo` addon flag) lists every objective with its state (Healthy / Warning / Breaching / Acknowledged / No data), error-budget bar, and fast/slow burn, and lets you create/edit/delete objectives and acknowledge a breach. **No data** is distinct from Healthy: an objective whose dimension never matched any metric (a typo'd job type or queue) reads as No data instead of a false green, and never alerts. Objectives are validated on save — a rate target must be a 0–1 ratio, a window must be positive — so a malformed objective can't silently disable its own evaluation. The detail page (`/slo/:id`) shows the budget gauge, burn stats, and the window/fast-burn breakdown. `GET/POST/DELETE {prefix}/api/slo*` serve the same data in any `AddWarp` process.

## Migration

Additive — two new tables (`slo_definition`, `slo_evaluation`), picked up by a standard `dotnet ef migrations add` / `database update`. Disable with `SloEvaluationInterval = null`, or simply never call `AddSlo()`.
