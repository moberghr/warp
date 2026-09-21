# Separate queue table — eligibility moves off the wide `job` row

- **Date:** 2026-09-20
- **Scope classification:** schema change (one new entity) + claim-path change + recovery-path change
- **Security impact:** none — the queue row carries `JobId`, `Queue`, `ScheduleTime`, `FetchedAt` and no payload, no PII, no new log or metric dimension (§1.2)
- **Schema impact:** **one new table**, plus a backfill for rows already `Enqueued` at upgrade. No column added to `job`, no column removed from it. Two `job` indexes become droppable in a **later** release, not this one.
- **Prior art:** PA-01 in the prior-art ledger — two comparable engines separate the queue, one does not
- **Evidence:** every number below is from `docs/perf-results.md`, PostgreSQL 18
- **Status: NOT ADOPTED — do not build from this document without reading the verdict first.**

---

## Verdict (2026-09-21): the throughput case did not survive testing

**Read this before anything below.** The design here is sound and the constraints in it are measured,
but the *reason* for building it was refuted after it was written.

The case rested on today's claim degrading as `job` fills with dead tuples — 19x across a 0-69% sweep,
with the claim's throughput ceiling crossing below the system's own ~925 jobs/sec somewhere around
25-35% dead. All of that was measured in an isolated plpgsql harness. **It does not transfer to the
real worker.** With autovacuum disabled on `warp.job` so bloat climbs monotonically and no vacuum I/O
confounds it, 200,000 jobs ran at a mean of **767 jobs/sec between 17-43% dead and 795 jobs/sec
between 58-67% dead** — no trend, at a final 68% dead, which is worse than the 43% recorded in the
§6.2.1 incident.

The most likely reason the two disagree is the **held snapshot**: the harness holds one open
permanently, the real run holds none, so PostgreSQL can set LP_DEAD hints in the real run and skip
dead index entries almost free. That is the same factor the earlier work found was *necessary* to
reproduce the collapse at all. A standing 50,000-row backlog and the absence of a handler are the
other untested candidates.

**What is still true and measured:** −28% WAL per job, `job` updates 3.0 → 1.0 per job, −10% on table
plus index size, and roughly −14% DB ms/job by extrapolation. **What is not:** any throughput gain, in
healthy or bloated conditions.

**The one measurement that could revive this** is whether the collapse reproduces end-to-end, through
the real worker, *with a snapshot held open*. Until that is run, the honest position is that PA-01
buys database cost and not throughput, and that Warp can no longer cause the held-snapshot condition
itself — only host code sharing the database can (see the audit in `docs/perf-results.md`).

**Implementation state.** Roughly a third is written, committed on `feat/separate-qeueue` and
deliberately **not merged**: the entity, queue rows at all eight insert sites, the three
`→ Enqueued` transition sites, and finalisation on both worker paths. It is tested green on both
providers and validated at 150,000 jobs with zero orphaned rows, but it maintains a table nothing
reads, so merging it would cost every consumer a migration and the single-worker path a round trip per
finalisation for no benefit. Resume from those commits if the verdict ever changes.

---

## Governing principle

**The claim must not read or write the table that job payloads live in.**

Everything else here follows from that. The wide `job` row is large (~500 B, dominated by `Message`),
carries six indexes, and takes two non-HOT updates per job — so it accumulates dead tuples faster than
anything else in the schema, and the claim currently scans the range where they accumulate. Moving
eligibility to a narrow table does not make the claim *faster* in a healthy database; it makes the
claim **independent of how bloated `job` is**.

## Problem

§6.2.1 records a queue draining at **567 jobs/sec falling to 27**, with the claim going 3.3 ms to
253 ms at 43% dead tuples. That is the failure this addresses. It is a degraded-state problem, and in
a healthy database none of this changes anything measurable.

### What was verified, and what turned out to be false

The ledger's stated mechanism — that separating the queue lets the claim stop invalidating index
entries — **does not survive measurement**. Gate 2 compared today's schema against both queue-table
shapes over 20,000 job lifecycles:

| metric, 20,000 jobs | today | queue table, stamp | queue table, delete |
|---|---:|---:|---:|
| wall ms (median of 3) | 1,296 | 1,020 | 728 |
| WAL bytes/job | 3,682.8 | 2,668.2 | 2,338.4 |
| `job` updates | 40,000 | 20,000 | 20,000 |
| **HOT updates** | **0** | **0** | **0** |
| `job` dead tuples | 40,000 | 20,000 | 20,000 |
| total dead tuples | 40,000 | **60,000** | 40,000 |

**No arm achieves a single HOT update**, because finalisation still writes `CurrentState` — needed by
the listing and parent indexes the dashboard and orchestrator depend on — and `ExpireAt`, also indexed.
Those indexes cannot move to the queue table. The gain is real but comes from **fewer and narrower
writes**, not from HOT: halving the wide-row updates halves the expensive dead tuples and removes two
indexes from every insert.

Note also that stamping generates **more total garbage than today** (60,000 against 40,000) while still
halving it on `job`. That is the correct trade — garbage on a 50 B single-index table is not garbage on
a 500 B six-index one — but it must be stated, because "PA-01 reduces bloat" is false as a total.

### The collapse, reproduced

The reproduction needs a **held snapshot**, not merely dead tuples. A first attempt reached 68.8% dead
with autovacuum disabled and the claim did not degrade at all: PostgreSQL sets LP_DEAD hints on index
entries pointing at dead tuples, so later scans skip them free. An open snapshot prevents the hint,
because the tuples are not dead to everyone, and every scan must re-check each from the heap. §6.2.1's
`CounterAggregator` held one transaction open for **51 minutes** — it supplied both halves.

Claim latency, one held snapshot, 200,000 rows with a 50,000-row backlog:

| `job` dead% | today | queue table, delete-on-claim |
|---:|---:|---:|
| 0.0 | 1.067 ms | 0.184 ms |
| 83.6 | **21.258 ms** | **0.500 ms** |

Today degrades **20x**; the incident was 21x. As a claim-throughput ceiling that is **18,744 → 941
jobs/sec**, and the lab measures ~925 jobs/sec end-to-end — so at that bloat level the claim has just
become the binding constraint, which is why the collapse is a cliff rather than a slope.

### The audit that sets the urgency

Every `IServerTask` was checked for unbounded work inside its lock transaction (`LocksWithTransaction`
defaults true; only `MessageRouter` opts out). The aggregators are capped by #300, `ExpirationCleanup`
by `MaxSweepBatchesPerTick`, `Orchestrator` by `ServerTaskBatchSize`. `StatisticRollup` was unbounded
and is capped by the change that precedes this spec. **No path in Warp still holds a transaction for
more than seconds.**

So this is **insurance, not a fix** — but not optional insurance. Warp's outbox is deliberately on the
host's `TContext` and its multi-application stance is an explicitly **shared database** (§8.23), so a
long transaction in the host's own code, a reporting query, or `hot_standby_feedback` on a replica
pins the horizon for `job` exactly as the aggregator did. Warp cannot bound any of those.

---

## Design

### 1. Stamp on claim, never delete

Delete-on-claim measured best on every axis and **must not be built here**. With the claim deleting the
queue row and never touching the job row, a worker that dies mid-execution leaves the queue row gone,
`job.CurrentState` still `Enqueued`, `LastKeepAlive` null — and `StaleJobRecovery`, which looks for
`CurrentState = Processing AND LastKeepAlive < cutoff`, matches nothing. The job sits in `Enqueued`
forever, healthy-looking on every surface.

**The prior art that uses delete-on-claim is safe for a reason that does not transfer.** It stamps a
second durable record first: a master row carrying an in-flight status *and* a deadline lease, written
before the transport row is claimed. A sweep finds lapsed leases whose owner stopped being live and a
re-assign step rebuilds a fresh transport row from the master. The deleted row is never recovered —
the master row is the recovery record, and the transport tier is explicitly an ephemeral buffer. (The
same codebase has a second path with no such record, where a crash loses the job outright; its own
notes acknowledge that gap.)

Warp's equivalent of that master row is the **wide `job` row**. So a faithful delete-on-claim here
means a write to `job` at claim time — the exact write this separation exists to remove. It would at
least be HOT-eligible (`LastKeepAlive` and `CurrentWorkerId` are in none of `job`'s six indexes), but
HOT still copies the tuple: ~500 B on a six-index table against ~50 B on a one-index table for the
stamp. And the claim would stop being independent of `job`, which is the property that makes it immune
to `job`'s bloat.

**So the queue row is the recovery record.** It survives the claim, carries `FetchedAt`, and a stale
`FetchedAt` makes the row claimable again with no sweep at all.

> The measured delete-on-claim arm wrote no lease at all, so it modelled the unsafe path and its
> advantage over stamping is overstated by a write that was never performed. See the correction in
> `docs/perf-results.md`. The general rule is the transferable part: **delete-on-claim is safe only
> where some other durable record is leased before the claim, and it is cheaper than stamping only
> where that record is cheap to write.**

### 2. `FetchedAt` is NOT in the index

This is the load-bearing decision and it is not obvious. Indexing `FetchedAt` so the claim can seek
directly to eligible rows means every stamp writes an indexed column — reintroducing, on the queue
table, the exact mechanism that degrades `job`:

| level | `job` dead% | today | `FetchedAt` **in** index | `FetchedAt` **out** of index |
|---|---:|---:|---:|---:|
| 0 | 0.0 | 1.518 ms | 1.173 ms | **0.627 ms** |
| 3 | 83.5 | **9.242** | 3.851 | **1.262** |

Indexing it buys only ~2.4x over today. Leaving it out makes the stamp **HOT-eligible** and buys
**7.3x**, with **82% of queue-table updates HOT** — the only non-zero HOT figure anywhere in this
investigation. Eligibility becomes a filter rather than a seek, which is affordable *because the queue
table holds only the backlog plus the rows actually in flight*.

**That bound is the design's load-bearing assumption and must be stated in the code.** A deployment
that prefetches deeply, or whose workers stall in bulk, widens the stamped-but-unfinalised set the scan
must skip. If prefetch ever becomes large relative to the backlog, revisit this.

### 3. `fillfactor = 70` on the queue table

Not optional. HOT needs free space in the page to write the new tuple version into; without it the
update falls back to non-HOT and the design degenerates to the middle column above.

### 4. Entity

`WarpJobQueue` in `Warp.Core.Data.Entities` (§8.13). **It must live in `Warp.Core`** —
`WarpStorageTypes.WarpProperties` selects by assembly equality, so an entity outside that assembly is
skipped by the §5.12 ownership pass and a consumer convention could retype its columns.

| column | type | notes |
|---|---|---|
| `Id` | `long` identity | PK |
| `JobId` | `Guid` | the job this makes claimable; no FK (see below) |
| `Queue` | `string` | matches `Job.Queue` |
| `ScheduleTime` | `DateTime` | claim ordering |
| `FetchedAt` | `DateTime?` | null = never claimed; **deliberately unindexed** |

One index: `(Queue, ScheduleTime)`. `WITH (fillfactor = 70)`.

**No foreign key to `job`.** An FK would make the queue insert depend on the job row's visibility and
would add a constraint check to the hot insert path; `ExpirationCleanup` already deletes job rows
leaf-first and would have to order around it. The orphan case (queue row whose job is gone) is swept,
not prevented — same stance as `RecurringJobLog.JobId`'s `SetNull`.

`WarpServerModelNames` and `WarpServerModel.MirrorNames` loop all entity types generically, so the
server-context mirror needs **no edit** — only the entity class, one `AddWarpJobQueueEntity` method,
and one line in `ServiceConfiguration.AddOutboxStateEntity`.

### 5. Claim

```
UPDATE job_queue SET fetched_at = now()
FROM (SELECT id FROM job_queue
      WHERE queue = $1
        AND (fetched_at IS NULL OR fetched_at < $2)   -- invisibility timeout
      ORDER BY schedule_time
      LIMIT $3 FOR UPDATE SKIP LOCKED) c
WHERE job_queue.id = c.id
RETURNING job_queue.job_id
```

Then a second statement fetches the job rows by id. That is one extra round trip per claim batch, which
the measurements say is affordable: the claim batch amortises it across `limit` jobs, and the wide-row
read was happening anyway (today's `RETURNING t.*` hydrates all 22 columns).

Scalar `queue` per statement, one statement per queue, exactly as the preceding claim change
established — the stale-estimate trap is orthogonal to this one and applies here too.

### 6. Rolling deploy — dual-read

A queue table has no precedent in `website/docs/releases.md`, which only ever documents additive
columns. Old processes claim on `job.CurrentState`; new ones claim on the queue table. Neither can be
assumed absent during a rolling deploy.

**v1 (this change):**
- Publishers write **both** the job row and a queue row, in the same `SaveChanges` — atomic by
  construction, since `Job.Id` is client-generated and both are on the caller's `TContext`.
- The claim reads the queue table **first**, and falls back to today's `job.CurrentState` claim when
  the queue table yields nothing. That catches rows enqueued by a process still running v0.
- The migration backfills queue rows for rows already `Enqueued`.

**v2 (a later release):** the fallback is deleted and the two eligibility indexes
(`(Kind, CurrentState, Queue, ScheduleTime)` and `(CurrentState, ScheduleTime)`) are dropped from
`job`. **Not in v1** — the fallback needs them, and dropping them is what makes the change irreversible.

The fallback is what makes v1 safely revertible: a v1 row is claimable by v0 code, because v1 still
writes `CurrentState = Enqueued` on the job row.

### 7. Recovery — and why the sweep this seemed to need does not exist

The obvious worry is a job reachable by nothing: non-terminal state, no queue row. It looked like it
needed a third bounded sweep to re-insert the queue row. **It does not, and the reason is the
invariant that makes this design safe — state it in code, because it is what the whole thing rests
on:**

> **Every transition of `job.CurrentState` commits in the same transaction as its queue-row change.**

That is already free, because both sides are always on one context:

| transition | job row | queue row | same transaction? |
|---|---|---|---|
| publish | INSERT `Enqueued` | INSERT | yes — caller's `TContext`, one `SaveChanges` |
| claim | *untouched* | UPDATE `FetchedAt` | n/a — only the queue row moves |
| finalise (terminal) | UPDATE `Completed`/`Failed`/`Deleted` | DELETE | yes — server context |
| finalise (requeue) | UPDATE `Enqueued`/`Scheduled` | UPDATE `FetchedAt = NULL` | yes — server context |

So a crash can only land *between* transactions, and every such point is already covered:

- crash after claim, before finalisation → queue row exists with a stale `FetchedAt`, and the
  invisibility timeout makes it claimable again with **no sweep at all**. This is the mechanism.
- crash mid-finalisation → the transaction rolls back; job stays `Processing` with a stale
  `LastKeepAlive`, which is exactly what `StaleJobRecovery`'s existing sweep is for.

`StaleJobRecovery` therefore keeps both its current responsibilities and gains nothing. The only new
sweep is a **defensive orphan sweep** for queue rows whose job no longer exists — the same shape and
justification as the `ErrorOccurrence` orphan sweep, and needed only because there is no FK.

**The atomicity invariant is the thing to guard in review.** Any future site that moves
`job.CurrentState` without moving the queue row in the same transaction reintroduces exactly the
silent-loss failure that rules out delete-on-claim.

---

## Blast radius

Enumerated against the code, not estimated.

**Enqueue sites** — every one must write a queue row. Beyond the obvious (`Publisher`,
`BatchPublisher`, `MessageRouter`, `RecurringJobScheduler`, handler outboxes), the audit found four
that are easy to miss:

- `Orchestrator.ActivateContinuationsAsync` — `Awaiting → Enqueued` via bulk `ExecuteUpdate`, announces nothing
- `WarpDispatcher.UnclaimUndelivered` — `Processing → Enqueued`, no transaction, silent
- `RecurringJobService.TriggerRecurringJob` — INSERT on `TContext`, not the server context
- `SagaStore.SaveChangesAsync` — a second commit point for outbox-staged rows
- plus requeue outcomes from `CircuitBreakerPipelineBehavior` and `SagaHandlerProxy`

**`NotificationDispatch.CapturePending`** keys entirely off `Entries<Job>()` where
`State == Added ∧ CurrentState == Enqueued`. It must walk the queue entity instead. The four
Modified→Enqueued sites already bypass it and hand-build notifications.

**Two contexts, one job.** The handler outbox commits on `TContext` while the same job's finalisation
commits on `IWarpServerContext` in a separate transaction. A requeue-outcome queue row lives on the
server context; a handler-published child's on `TContext`.

**`ExpirationCleanup`** needs a queue-row delete in **both** delete paths — the file already warns that
a new delete path missing one silently loses what the other preserves.

**Tests:** ~67 files seed `Job` rows directly through a raw `DbContext`, bypassing `IPublisher` — 489
`Set<Job>().Add*` sites and 271 `CurrentState.ShouldBe` assertions. Heaviest:
`Orchestration/OrchestrationTaskTests.cs` (56), `Admin/JobCommandServiceTests.cs` (51),
`Admin/JobQueryServiceTests.cs` (39). A seeding helper that writes both rows absorbs most of it; the
assertions mostly stand unchanged, since `job.CurrentState` keeps its meaning.

---

## What this does not solve

- **Throughput.** Unchanged, in every arm, healthy or degraded-but-recovered. Database execution is
  ~4% of the per-job worker budget; halving it moves jobs/sec ~2%.
- **HOT updates on `job`.** Still zero. Finalisation writes `CurrentState` and `ExpireAt`, both indexed.
- **The stale-statistics claim cliff.** Fixed separately by the scalar predicate; the queue table would
  also fix it, but that is already bought.
- **SQL Server.** Every measurement here is PostgreSQL. The mechanism is cost-model and
  visibility-check behaviour, both of which SQL Server has analogues for, but none of it is measured
  there and the lab's `--sqlserver` arm takes no DB-side statistics.

## Questions raised, and how they were settled

1. **The unreachable-job sweep** — **does not exist.** It dissolved once the atomicity invariant in
   Design §7 was written down: every `CurrentState` transition already commits with its queue-row
   change, so the state it was meant to repair is unreachable. What remains is a defensive orphan
   sweep for queue rows whose job is gone, which is bookkeeping rather than recovery.
2. **Invisibility timeout** — **reuse `WarpConfiguration.InvisibilityTimeout`.** It already exists and
   already means "a claim older than this is presumed dead" for `StaleJobRecovery`. A second knob with
   the same meaning would be a configuration trap: the two would drift and the shorter would silently
   win.
3. **Prefetch interaction** — **documented, not capped.** Design §2's filter skips stamped rows, and
   the stamped-but-unfinalised set is bounded by `WorkerCount + PrefetchCount` per group — tens, against
   a backlog in the thousands. A cap would be guessing at a threshold nothing has hit; the assumption
   goes in the code comment so the next person measuring a slow claim knows where to look.
4. **Fallback default** — **on, unflagged, for v1.** A flag here would have exactly one safe setting,
   and the fallback is what makes v1 revertible. It is deleted in v2, not configured.
