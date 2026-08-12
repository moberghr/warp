# Plan — Requeue / outcome metrics taxonomy

Spec: `docs/specs/2026-08-05-requeue-outcome-metrics.md`
Sidecar: `docs/specs/2026-08-05-requeue-outcome-metrics.json`

Nine batches across two PRs. R1–R3 land on `fix/requeue-stats` and are independently shippable; T1–T6 land on `feat/outcome-reason-metrics`, cut after PR1 merges.

**Governing principle** — a metric records an event; current state is a query. R3 makes that true by removing the only exception.

**Global constraints on every batch**

- Worker hot path (§0.2/§6.1): the only additions are reading `outcome.Reason`, reading an already-captured attempt-count local, a `switch` over a closed enum, and `Counter` rows joining the **existing** `SaveChanges`. No new query, no round-trip.
- Both worker paths change together, always (§8.29 precedent).
- Tests on both providers via `[GenerateDatabaseTests]` (§4.2); bare `[TimedFact]`.
- Analyzer-clean build; `TreatWarningsAsErrors=true`.
- Each batch closes with a grep of the **whole** test tree for assertions on the behaviour just changed.

---

## PR1 — `fix/requeue-stats`

### R1 — Fix `willRetry` mislabeling
`WarpWorkerService.cs:365` and `WarpDispatcherWorker.cs:412` must test `State.Enqueued or State.Scheduled` — the pair the DB branch at `:612` already uses. **Boundary:** label computation only. **Done when** a default-`Delays` case asserts `status=retried` in both paths and the existing empty-delays case stays green.

### R2 — Count manual and crash-recovery requeues
`RequeueJob` (`:108`) and `BulkRequeueJobs` write **both** `stats:requeued` and `stats:requeued-manual` (+hourly); `StaleJobRecovery` (`:93-134`) writes `stats:requeued` + `stats:requeued-recovery`, one row carrying the batch count. Bulk increments come from `flippedIds.Count` inside `AddRequeueLogsForFlipped` (`:631`) — **not** from `ApplyRequeueAccounting`'s `affected`, which can differ. **Invariant:** increments == `Requeued` JobLog rows written.

### R3 — Remove the decrement
Delete `DecrementStatForState` (`:723`), its two call sites (`:89`, `:134`), and the decrement arm of `ApplyRequeueAccounting` (`:614-625`). Five tests pin the `-1` and invert: `JobCommandServiceTests.cs:411, 412, 493, 759` and `StatCounterTests.cs:45`. **Boundary:** the *increments* are untouched; only the negative rows go. **Done when** no `Counter` row with a negative value is written anywhere, lifetime totals reconcile with their hourly buckets, and the dashboard's `Math.max(0, …)` clamp is provably unreachable from a requeue.

---

## PR2 — `feat/outcome-reason-metrics`

### T1 — Enum, `Reason`, token map, 12 stamps
`OutcomeReason` (from 1), `JobOutcome.Reason` (`init`, nullable, additive), `OutcomeStatKeys` switch token map. Stamp all 12 sites, including the **new** explicit retry-exhausted outcome replacing the fall-through — state-preserving, but it newly re-serializes metadata at `WarpWorkerService.cs:355` with identical values. **Boundary:** no worker changes; keys defined, not written.

### T2 — Worker key composition
Inside the existing `SaveChanges`: `stats:unsuccessful` on `Failed`/`Deleted`; `stats:{state}-{reason}` when a reason is present; `stats:retried-jobs` when `Reason == Retry` and the incoming attempt count is 0. Capture that count at the existing metadata read (`:190` / `:247`) — do not re-read at finalization. **Boundary:** existing totals keep their values; the happy path writes no extra rows.

### T3 — `warp.job.requeued` meter
`Counter<long>`, tags `queue`/`type`/`reason`/`application`, always-on, emitted at the same site. **Boundary:** no key/PII tags (§1.2).

### T4 — Counters page
Replace the flat alphabetical list with the outcome hierarchy (umbrella → state total → reasons), state that these are recorded events that only increase and point at the dashboard for current state, give the new keys intentional colours in `builtInColors` (they currently hash via `colorFor`), and show the unattributed remainder rather than hiding it. **No derived readouts** — the page stays recorded rows only; a recovery-rate tile was rejected because the difference it would compute is not the recovered count. Demo fixtures updated so the page renders without a live server.

### T5 — Mixed-workload stat-surface verification
One workload exercising every outcome class — success, plain failure, retry-then-succeed, retry-exhausted, mutex skip, rate-limit wait, timeout delete, manual requeue, cancellation — then assert the **exact value of every `stats:` key**, not a subset, plus the invariants (totals = Σ reasons + unattributed; `unsuccessful == failed + deleted`; `retried-jobs` == distinct jobs retried). N=2 per case so assertions are exact and it is not a spray-N test (§0.4). Boots a full server, so it joins the serialized `HeavyIntegration` collection (§4.7.1) and caps `WorkerCount`. A Playwright spec then confirms the Counters page renders the same numbers in the grouped hierarchy.

This batch is the capstone: it is the only place the whole surface is verified end to end rather than key by key.

### T6 — Docs + rules
`queue-metrics.md` gets the metric-vs-query principle, the three-level model, the bounded-reason constraint and the volume figures. **Release notes for both behaviour changes** — the `willRetry` label and the decrement removal. `CLAUDE.md` + rules entry. Full suite, both providers, at the end.

---

## Post-implementation review items

- Both worker paths emit identical key sets — diff them explicitly.
- No enum member lacks a token; no token collides.
- No new PII/unbounded tag reached a meter.
- No negative `Counter` row anywhere.
- Happy-path row count per completed job unchanged from `main`.
- Full suite, both providers, once at the end — not a namespace subset.
