# Requeue / outcome metrics taxonomy

- **Date:** 2026-08-05
- **Scope classification:** enhancement (additive) + one bug fix + one deliberate behaviour change
- **Security impact:** none (new meter tags are deliberately key-free; see Risks)
- **Schema impact:** none — no new entity, no new column, no migration
- **Delivery:** two PRs (R1–R3 → PR1 on `fix/requeue-stats`; T1–T6 → PR2 on a `feat/` branch)

---

## Governing principle

**A metric records an event. Current state is a query.**

Warp's `stats:` family currently violates this in one place, and every inconsistency found while planning traces back to it. Three keys — `stats:succeeded`, `stats:failed`, `stats:deleted` — are **restated**: `DecrementStatForState` (`JobCommandService.cs:723`) and `ApplyRequeueAccounting` (`:614`) write `-1` when a requeue undoes a terminal outcome. Everything else is append-only.

That restatement produces four distinct defects:

1. **A key means two things depending on its suffix.** The decrements write only the *lifetime* key, never an hourly bucket (and `DeleteJob:94` likewise increments lifetime-only). So `stats:failed` and the sum of its own `stats:failed:{hour}` buckets already disagree on `main`, silently.
2. **Rate math is computed from a non-monotonic counter.** `src/ui/src/stores/dashboard.ts:51` charts `Math.max(0, current.failed - prev.failed)`; a requeue drives that delta negative and the clamp silently floors it, under-reporting the tick.
3. **It destroys the counter's only unique information.** Failed jobs never auto-expire (§8.2), so the `Failed` *query* already answers "currently failed" permanently and accurately. The one thing a counter could add is "ever failed" — and the decrement is exactly what removes it, converging the counter on the query's answer from a worse source.
4. **The breakdown keys can have no decrement counterpart.** Nothing stores which reason produced a job's previous terminal state, so a restated total can never be reconciled with an append-only breakdown.

**Resolution: remove the decrement (R3).** All `stats:` keys become append-only. Each surface then carries exactly one kind of number:

| Surface | Answers | Source |
|---|---|---|
| Nav badges (`MainLayout.tsx:226-257`) | what's happening now | `Job` queries |
| Dashboard tiles | what's happening now | `Job` queries |
| Dashboard realtime chart | rate of change | counters (now legitimately monotonic) |
| Counters page (`useCounters` only) | what has happened | append-only counters |

The Counters page is the surface that most needs this: it shows `stats:failed` with no live query beside it, so a reader there has no way to tell the number has been rewritten by requeues.

## Problem

Beyond the restatement above, the flat `stats:requeued` key cannot answer:

1. **Why** was a job requeued or dropped? Retry backoff, mutex Wait, rate-limit Wait and saga conflicts collapse into one number; `stats:deleted` mixes user deletes, mutex Skip, rate-limit Skip and timeout Delete.
2. **How many jobs** (not events) are thrashing? A job retried 15 times counts 15 times.
3. **Did retries help?** Exhaustion is signalled by *absence* — `RetryPipelineBehavior` sets no outcome on the last attempt, so the worker's `else` sets `State.Failed`, identical to a job with no retry policy.

Plus two counting gaps and one bug:

- **`willRetry` mislabels every default retry as a failure.** Both worker paths compute `willRetry = job.CurrentState == State.Enqueued` (`WarpWorkerService.cs:365`, `WarpDispatcherWorker.cs:412`), but `JobOutcome.RescheduledState` returns `State.Scheduled` for any future target (`JobOutcome.cs:76`) and `RetryOptions.Delays` defaults to `[15, 60, 300]`. Every default retry is emitted to OTel as `status=failed`; `warp.job.retried` never fires. **The DB disagrees with OTel** on the same attempt.
- **The one test covering it dodges it.** `OTelMetricsTests.GetAndProcessJob_FailedWithRetries_RecordsRetriedStatus` passes only because `CreateWorker` sets `o.Delays = []`.
- **Manual and crash-recovery requeues are uncounted.** Both write a `Requeued` `JobLog` and no `Counter`.

Concurrency and rate limit emit rich **spans** (`ConcurrencyPipelineBehavior.cs:47-61`, `RateLimitPipelineBehavior.cs:55-116`) but **no meter** — sampled trace detail, not countable. Sagas are the only mechanism with a requeue counter.

## Solution

Stamp a **bounded reason** on the `JobOutcome` each behaviour already constructs, then **compose** the key from `(terminal state, reason)` at the two worker finalization sites. Nothing enumerates key names; only combinations that occur materialise rows.

Three levels, each written independently — with the decrement gone, the totals also reconcile exactly with their parts:

> **Revised during code review.** `stats:unsuccessful` below was specified as a **stored** row. It shipped **derived** instead: no such `Counter` row is ever written, and the Counters page computes it as `failed + deleted`. The stored version was written at only the two worker finalization sites, while `failed`/`deleted` also move at six others (`DeleteJob`, `BulkDeleteJobs`, both crash-recovery arms, both worker cancellation paths) — so it under-reported from the first delete onward and the UI rendered a child larger than its parent. A pure function of two stored values should not be a third stored value. Every `stats:unsuccessful` reference below reads as *the derived umbrella*.

```
stats:unsuccessful              every terminal outcome that is not Completed — DERIVED on read
stats:failed / stats:deleted    per-state totals (append-only after R3)
stats:failed-retry-exhausted    per-reason breakdown
stats:deleted-concurrency
stats:requeued-ratelimit
```

Plus `stats:retried-jobs` — incremented only on a job's **first** retry.

And `warp.job.requeued{queue, type, reason, application}`, filling the countable-metric gap.

### Why `Reason` on `JobOutcome`

`JobOutcome` is already the pipeline→worker channel. The behaviour that made the decision is the only component that knows why, and constructs the outcome anyway — one added `init` property, read off an object already in scope. The worker learns nothing about any addon; the hot path (§0.2/§6.1) gains one field read and one switch.

### Attempt count: reuse the existing read

Both worker paths *already* read the attempt count from the metadata dict via a pinned constant, precisely to respect the worker↔addon dependency wall:

```csharp
// WarpWorkerService.cs:190 / WarpDispatcherWorker.cs:247
if (jobContext.Metadata.TryGetValue(WarpTelemetryAttributes.RetryMetadataRetriedTimesKey, out var retriedTimesObj)
    && retriedTimesObj is long retriedTimes)
```

pinned by `WarpTelemetryTests.cs:269` and recorded as a lesson (`tasks/lessons.md`, 2026-05-07). It runs **before** handler execution, so a requeue outcome with `Reason = Retry` and an incoming count of `0` is exactly "first retry". No `JobOutcome.Attempt` property is added; the public API addition is `Reason` alone.

### Worked examples

**100 jobs throw, retry, then succeed:**

```
stats:succeeded       100      stats:requeued-retry   100
stats:failed            0      stats:retried-jobs     100
stats:unsuccessful      0      stats:requeued         100
```
Correct — they never reached a terminal failure. `retried-jobs` is what makes them visible at all.

**100 jobs fail, are requeued from the dashboard, then succeed (post-R3):**

```
stats:succeeded       100      stats:requeued-manual  100
stats:failed          100      stats:requeued         100
stats:unsuccessful    100      stats:retried-jobs       0
```
`stats:failed` stays 100 — they *did* fail. The live `Failed` tile reads 0 from the `Job` query, which is the number that should be 0. Before R3, `stats:failed` also read 0 and the history was lost.

## Design

### `OutcomeReason` enum (`Warp.Core.Enums`, from 1 per §8.11)

| Member | Token | Set by |
|---|---|---|
| `Retry = 1` | `retry` | `RetryPipelineBehavior` reschedule |
| `RetryExhausted = 2` | `retry-exhausted` | `RetryPipelineBehavior` **new** explicit exhausted outcome |
| `Concurrency = 3` | `concurrency` | `ConcurrencyPipelineBehavior` requeue + skip |
| `RateLimit = 4` | `ratelimit` | `RateLimitPipelineBehavior` contention / throttled / skip |
| `Timeout = 5` | `timeout` | `TimeoutPipelineBehavior` Delete mode |
| `Saga = 6` | `saga` | `SagaHandlerProxy` busy / conflict / not-found / expired-timeout |
| `Manual = 7` | `manual` | `JobCommandService` requeue paths (not via `JobOutcome`) |
| `Recovery = 8` | `recovery` | `StaleJobRecovery` (not via `JobOutcome`) |

Tokens come from a `switch` expression, **never** `ToString().ToLowerInvariant()` — no per-finalization allocation, and the wire format survives an enum rename. A guard test asserts every member maps to a distinct token.

**Boundedness is load-bearing.** `JobOutcome` is public API; a free-string reason would let a caller mint unbounded `Statistic` rows.

### Outcome-construction sites (12)

`RetryPipelineBehavior.cs:67` reschedule · **new** exhausted branch · `ConcurrencyPipelineBehavior.cs:86` `BuildRequeueOutcome` · `:95` `BuildSkipOutcome` · `RateLimitPipelineBehavior.cs:75` lock-contention · `:100` throttled Wait · `:110` skip · `TimeoutPipelineBehavior.cs:69` Delete · `SagaHandlerProxy.cs:262` busy · `:273` `BuildRequeueOutcome` · `:287` `BuildNotFoundOutcome` · `:294` `BuildExpiredTimeoutOutcome`.

The exhausted branch currently sets no outcome. An explicit `State = State.Failed` outcome is state-preserving, but re-serializes metadata at `WarpWorkerService.cs:355` (identical values) and routes through `ClearHandlerType`/`ScheduleTime` handling — regression-tested.

### Worker composition

Inside the existing `SaveChanges` (no extra round-trip):

- ~~`stats:unsuccessful` + hourly, when state is `Failed` or `Deleted`~~ — **not implemented**, see the revision note above; the umbrella is derived on read from the two totals, so the worker writes nothing for it
- `stats:{stateToken}-{reasonToken}` + hourly, when a reason is present
- `stats:retried-jobs` + hourly, when reason is `Retry` and incoming attempt count is 0

Both requeue-writing paths write the **state total and the breakdown** — manual requeue writes `stats:requeued` *and* `stats:requeued-manual`, matching crash recovery. Happy path is unchanged: a plain completion carries no reason and writes exactly what it writes today.

### Meter

`warp.job.requeued` — `Counter<long>`, tags `queue`, `type`, `reason`, `application`. Always-on. **No key tags** — `ConcurrencyKey`/`RateLimitKey` are unbounded and PII-adjacent (§1.2) and stay on spans.

### Counters page

Restructured from a flat alphabetical key list into the outcome hierarchy (umbrella → state total → reasons), with a one-line statement that these are recorded events that only ever increase and that current state lives on the dashboard. New keys get intentional colours in `builtInColors` (they currently hash to arbitrary hues via `colorFor`). **No derived/computed readouts** — the page shows recorded counter rows and nothing else. A "recovery rate" tile was considered and rejected: `retried-jobs − failed-retry-exhausted` is not the recovered count, because that difference also contains jobs still retrying, jobs deleted mid-retry by a timeout or concurrency skip, and jobs short-circuited to `Failed` by another reason. A correct recovery metric would need its own key (increment at the `Completed` branch when the incoming attempt count is > 0) — not built here.

### Retention / volume

`stats:` keys stay lifetime + unmarked hourly, so `CounterAggregator` and `StatisticRollup` need **zero changes**. 14 new keys × ~252 retained buckets (1 lifetime + 168 hourly@7d + 83 daily@90d) ≈ **3.3k rows, ~400 KB, constant with respect to job throughput.**

## Success criteria

| ID | Criterion | Verification |
|---|---|---|
| RSC1 | A **default-delay** retry emits `status=retried`, not `failed`, on `warp.job.completed` + `warp.job.duration`, in **both** worker paths | `OTelMetricsTests` (new default-delay + dispatcher cases) |
| RSC2 | `RequeueJob` and `BulkRequeueJobs` write `stats:requeued` **and** `stats:requeued-manual` (+hourly), each equal to the number of `Requeued` `JobLog` rows written | `RequeueStatsTestsBase` (PG + SQL Server) |
| RSC3 | `StaleJobRecovery` requeue arm writes `stats:requeued` + `stats:requeued-recovery`; Failed/Deleted arms unchanged | `RequeueStatsTestsBase` (`CrashRecoveryTests` planned, but the cases landed here) |
| RSC4 | No `stats:` key is ever decremented; `DecrementStatForState` is gone and no negative `Counter` row is written by any requeue path | `JobCommandServiceTests` (inverted), `StatCounterTests` (inverted) |
| RSC5 | Every `OutcomeReason` member maps to a distinct lowercase token; no member unmapped | NoDb guard test |
| RSC6 | **Revised:** the umbrella is **derived** on read as `failed + deleted` and **never stored** — no `stats:unsuccessful` `Counter` row exists after any workload | `OutcomeMetricsTestsBase.AssertUmbrellaIsNotStored` (all 4 cases) + the live e2e umbrella test + `CountersPage.buildRows` |
| RSC7 | `stats:retried-jobs` increments once for a job retried N>1 times; not at all for a first-attempt failure with no retry policy | `OutcomeMetricsTestsBase` |
| RSC8 | Each of the 12 sites stamps its reason; retry-exhausted yields `State.Failed` + `stats:failed-retry-exhausted` with unchanged state/handler-type behaviour | Per-addon tests + `RetryTests` regression |
| RSC9 | Dispatcher mode (`UseDispatcher = true`) produces byte-identical keys to single-worker mode | Dispatcher-mode integration test |
| RSC10 | `warp.job.requeued` emits with `reason` and **no** key/PII tag | `OTelMetricsTests` via `MeterListener` |
| RSC11 | Mixed-workload surface test asserting the **exact expected value for every `stats:` key** in one run, plus every invariant | `StatSurfaceTestsBase` (PG + SQL Server) — nine outcome classes × N=2, complete lifetime **and** hourly key/value maps asserted by equality, plus: umbrella derived not stored, per-state total = reasons + unattributed remainder, `retried-jobs` (distinct jobs) < `requeued-retry` (events), no negative row |
| RSC12 | The Counters page renders the workload's numbers in the grouped hierarchy | `src/ui/e2e-live/outcome-stats.spec.ts` against the **live** Aspire stack, not the planned demo fixtures (whose hand-written numbers could never catch a metrics bug). On demand: `npm run test:e2e:live` |
| RSC13 | Solution builds analyzer-clean; full suite green on both providers | `dotnet build src/Warp.slnx`, full `dotnet test` |
| RSC14 | Frontend builds | `npm run build` in `src/ui` |

## Out of scope

- Any schema change (no `Job.RequeueCount` column, no new entity)
- A dimensioned `requeue:{queue}:{type}:{reason}` DB key family — the meter's tags cover crossed breakdowns; the DB family would cost ~378k rows
- Mutex-vs-semaphore reason split (undecidable at the site — see Rejected alternatives)
- Per-queue / per-type requeue breakdown **in the DB** (meter only)
- Sink-gating `stats:*` under `JobMetricsSink = Otel` (follow-up)
- Resetting `RetriedTimes` on manual requeue (pre-existing behaviour; documented, not changed)
- Changing the dashboard's live-state tiles or nav badges (they already query `Job` and are correct)
- Reverse navigation from a metric to its jobs

## Assumptions

- `[VERIFIED:src/core/Warp.Worker/WarpWorkerService.cs:190]` both worker paths already read `RetriedTimes` as `long` via the pinned key, guarded by `WarpTelemetryTests.cs:269` — reused rather than re-read.
- `[VERIFIED:src/core/Warp.Core/Retry/RetryOptions.cs]` `Delays` defaults to `[15,60,300]`, so the default retry path is `State.Scheduled`.
- `[VERIFIED:src/core/Warp.Core/Services/DashboardStatsService.cs:50-100]` the dashboard payload mixes `Job` queries (live state) with `GetCombinedStatValue` counters (lifetime totals); `CountersPage.tsx:5,30` uses counters only.
- `[VERIFIED:src/ui/src/stores/dashboard.ts:51]` the realtime chart deltas `totalFailed` with a `Math.max(0, …)` clamp — non-monotonic today.
- `[VERIFIED:src/tests/.../JobCommandServiceTests.cs:411,412,493,759 + StatCounterTests.cs:45]` five assertions pin the `-1` decrement; R3 inverts them.
- `[VERIFIED:src/core/Warp.Core/Data/Entities/Statistic.cs]` `Statistic` is `Key` + `long` — the volume estimate holds.

## Risks

- **R3 changes the meaning of three existing metrics.** `stats:succeeded`/`failed`/`deleted` become "ever" rather than "latest outcome was", so they get larger for anyone who requeues. Five tests invert; needs a release note. The live dashboard tiles and nav badges are unaffected (they query `Job`).
- **Reason boundedness is the cost model.** A future free-string reason turns 14 keys into keys-per-value. Closed enum + guard test mitigate.
- **Both worker paths must move in lockstep** (§0.2). §8.29 records the dispatcher path being missed once; RSC9 is the guard.
- **The retry-exhausted branch newly sets an outcome**, re-serializing metadata on that hot-path branch. Behaviour-preserving by construction; regression-tested.
- **The `willRetry` fix changes an emitted metric value.** Deployments alerting on `warp.job.completed{status=failed}` will see failures drop and `retried` appear.
- **The mixed-workload test needs exactness, which rules out a booted server.** It shipped on direct-drive `WarpWorkerService` iterations, not `WarpTestServer`: a concurrency-`Wait` outcome returns the job to `Enqueued` with `ScheduleTime = now`, so a polling worker re-requeues it for as long as the slot is held and the count becomes wall-clock dependent. It uses N=2 per case (not 50) so assertions stay exact (§0.4), and still joins the serialized `HeavyIntegration` collection (§4.7.1) — it is the heaviest DB test in its namespace and thread-pool starvation from a neighbouring server host is what would push it past its budget.
- **`stats:*` is not sink-gated**, so Otel-only deployments still pay these rows. Pre-existing; noted as follow-up.

## Rejected alternatives

- **`Job.RequeueCount` column + 0→1 conditional.** Would count distinct requeued jobs across *all* mechanisms and make "requeued more than 3 times" a plain filter. Rejected: no schema change. `RetriedTimes` in `Job.Metadata` covers the retry case.
- **Dimensioned `requeue:` key family.** ~378k rows (~45 MB) plus a parser, query method, route and chart, for what the meter's tags already give.
- **Redefining `stats:failed` to include Deleted outcomes.** Breaks its correspondence with `State.Failed`. Replaced by `stats:unsuccessful`.
- **Keeping the decrement and labelling the keys "restated" in the UI.** Considered and rejected: it leaves the lifetime-vs-hourly divergence, the non-monotonic rate, and the un-reconcilable breakdown in place, and teaches a distinction that only exists because of a redundant mechanism.
- **Mutex vs semaphore split.** The sole local discriminator is `effectiveLimit == 1`, which admin overrides (`ConcurrencyLimitResolver`, `:43`) can invert.
- **`ToString().ToLowerInvariant()` for tokens.** Allocates per finalization; couples wire format to member names.
- **`JobOutcome.Attempt` property.** Redundant — both workers already read the attempt count.
