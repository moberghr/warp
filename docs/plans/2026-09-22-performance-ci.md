# Plan — Performance CI: comparing two versions on GitHub runners

Scope: new-infrastructure. Branch: `ci/perf-comparison`.

Motivation: the 7.1.0 revalidation was done by hand over several hours, and it found two published
numbers that were wrong (a -26.7% that is really -17%) and one conclusion that was backwards (the
claim predicate's bad plan *is* reachable in the lab). Nothing in CI would have caught either. Every
performance number on the website is produced manually, which is why they drift.

## The constraint that decides the design

Not "CI is noisy, give up on timing" — that is too blunt, and BenchmarkDotNet exists precisely to beat
random noise. It sizes its own iteration count from a pilot run, warms up until JIT and tiering have
settled, removes outliers, and **reports the confidence interval instead of hiding it**. Its
`--statisticalTest` (Mann-Whitney U) is also self-calibrating as a gate: on a noisy runner the interval
widens and the test declines to call significance, so it errs toward *not firing* rather than
false-alarming. That is the right failure mode for a required check.

One BDN feature is worth the adoption on its own: **it detects multimodal distributions and warns.**
The 7.1.0 revalidation found the old claim predicate was bimodal — 2 of 6 runs took a catastrophic
plan, and an earlier session had missed it by comparing medians. BDN flags that shape automatically.
Median-reading is what failed here; BDN is the fix for it.

So the real question is not "is CI too noisy" but **"what effect size can be resolved, and how many
iterations does that cost?"** For comparing two means at 95% confidence and 80% power, per arm:

```
n  ~=  15.7 * (CV / effect)^2
```

| per-iteration CV | effect to detect | iterations per arm |
|---|---|---|
| 10% | 5% | ~63 |
| 20% | 5% | ~251 |
| 20% | 20% | ~16 |

**That formula is the whole design.** It says nothing about whether the hardware is good; it says what
the measurement costs at a given timescale:

- **Microbenchmarks** (mediator dispatch, DI scope creation, metadata serialization) run in
  microseconds. 251 iterations is free, so BDN resolves a 5% regression on `ubuntu-latest` without
  difficulty. **Time is a legitimate gate here** and this plan gates it.
- **Server arms** (drain 10,000 jobs against Postgres) take ~30 s per iteration. 251 iterations is two
  hours *per arm*, which no PR gate can afford — and the residual variance there is dominated by the
  database and the container rather than by anything BDN can warm up. At the 5-10 iterations actually
  affordable, the resolvable effect is tens of percent, which is far coarser than the regressions worth
  catching.

Hence the split: **gate time where iterations are cheap, gate counts where they are not.** Counts carry
the load in the second tier not because averaging is invalid, but because they need no averaging at
all. From the 7.1.0 revalidation, measured on a quiet machine:

| quantity | observed spread | gate |
|---|---|---|
| statements per job | 0.1-2.7% within a run, <=5% between passes | **hard gate** |
| rows inserted/updated/deleted | exact | **hard gate** |
| allocated bytes per operation (BDN) | deterministic modulo GC | **hard gate** |
| query plan shape | exact, given fixed statistics | **hard gate** |
| microbenchmark time (BDN, statistical test) | resolvable to ~5% | **gate** |
| buffers/blocks read | a few % | warn |
| server-arm DB ms/job | up to **417%** | display only |
| server-arm jobs/sec | up to **56%** | display only |

Two further rules, both learned the hard way during the revalidation:

1. **Both versions must be measured on the same runner, in the same job, interleaved A/B/A/B.**
   Never compare a PR against a number stored from an earlier run on a different VM — that comparison
   measures the VMs, not the code. Interleaving is what cancels drift *within* the job, which is the
   component averaging cannot remove: a noisy neighbour arriving mid-run biases a whole block of
   iterations rather than scattering them.
2. **The harness must be identical across the two versions.** The 7.0.0 tag could not be benchmarked
   directly, because the lab's mutex/semaphore scenarios and `--warmup` were added *after* it. The
   comparison build has to be today's harness with the old *runtime* files swapped in.

## Batch 1 — Deterministic guards in the existing suite (no new infra)

These need no CI changes at all: they are ordinary tests in the existing matrix, they run on both
providers, and they close the two rot hazards 7.1.0 introduced without guards.

**Files:** `src/tests/Warp.Tests/Worker/CompletionColumnLockstepTestsBase.cs` (new),
`src/tests/Warp.Tests/Worker/ClaimPlanShapeTestsBase.cs` (new),
`src/tests/Warp.Tests/Worker/QueueOrderingTestsBase.cs` (new).

1. **`CompletionColumnLockstepTests` — the §6.10 hazard, currently unguarded.** `CompletionBatch`
   marks eight columns by hand; a field assigned at finalization and not added to that list is
   *silently never persisted*. Two arms:
   - *Behavioural:* populate a job with a sentinel in every mapped column, run it through the
     dispatcher completion path, assert the eight finalization columns changed and the other twelve
     are byte-identical to what was stored.
   - *Mechanical:* reflect over `Job`'s mapped properties and assert the set equals a declared
     partition of {finalization-assigned, untouched}. A new column on `Job` then fails this test
     until someone classifies it, which is the point — the hazard is a new field nobody thought about.
2. **`ClaimPlanShapeTests` (PostgreSql)** — seed a job table, `ANALYZE` while drained, flip rows to
   `Enqueued` without re-analyzing, then `EXPLAIN` the claim and assert the plan uses
   `ix_job_kind_current_state_queue_schedule_time` and contains **no `Sort` node**. This is the #301
   regression class made permanent: it fails if anyone reintroduces a multi-value queue predicate.
   SQL Server has no cheap equivalent assertion; document the asymmetry rather than fake it.
3. **`QueueOrderingTests`** — ordinal ordering across queues, and one claim statement per queue. 7.1.0
   moved this from database collation to `StringComparer.Ordinal` and nothing asserts it.

**Checkpoint:** `dotnet build src/Warp.slnx` + the full matrix. No CI change yet.

## Batch 2 — Statement budgets as ordinary tests

**Files:** `src/tests/Warp.Tests/TestData/CommandCountingInterceptor.cs` (new — port from
`Warp.PerfTest`, which already has one), `src/tests/Warp.Tests/Worker/StatementBudgetTestsBase.cs`.

A `DbCommandInterceptor` counting commands for one job end to end, asserting a per-job budget. This
catches an accidental N+1 on the hot path in normal PR CI, on both providers, with no benchmark
infrastructure.

**The trap:** server tasks tick on timers and add commands that have nothing to do with the job, so a
naive count is not deterministic. Bind the counter to the worker's own connection, or run the fixture
with time-driven tasks disabled (`HealthCheckInterval = null`, per §4.6), and assert a *range* rather
than an exact number.

**Checkpoint:** run the new tests 20x locally and confirm the count never moves before choosing the
budget. A budget picked from one run is a flake waiting to happen.

## Batch 3 — BDN microbenchmarks: gate allocations AND time (Tier A)

**Files:** `src/benchmarks/Warp.Benchmarks/*` (add `[MemoryDiagnoser]`, JSON exporter),
`.github/workflows/perf.yml` (new).

`Warp.Benchmarks` already has `MediatorBenchmark` and `WorkerDispatchBenchmark`. These run in
microseconds, so the iteration budget needed to resolve 5% is free — this is the tier where BDN's
machinery does exactly what it is built for:

- Gate on **`Allocated`** with a tight threshold (2%). Deterministic, so a tight bound is safe.
- **Gate on time** via `--statisticalTest 5%` (Mann-Whitney U). Let BDN decide significance rather
  than thresholding a raw percentage: if the runner is noisy the interval widens and the test simply
  declines to call it, which fails toward silence instead of toward a flaky red build.
- Fail the job on any benchmark BDN marks **multimodal**, rather than reading its median. That warning
  is what would have caught the bimodal claim plan the 7.1.0 revalidation found by hand.
- Set `MinIterationCount` high enough that a pilot run on a slow runner cannot settle for a sample too
  small to test, and record the achieved CI in the step summary so the gate's own resolution is visible.

Version comparison, in order of preference:

1. **BDN's own multi-version job** — `Job.WithNuGet("Moberg.Warp.Core", "7.0.0")` alongside the local
   build runs both in one process, interleaved, with the statistical test built in. Best available
   answer for this tier, and it sidesteps the "two runners" problem entirely. Requires the baseline on
   NuGet (7.0.0 is) and benchmark code that compiles against both APIs.
2. **Two git refs, one job** — build PR head and merge-base, run the same harness against each,
   compare the JSON artifacts. Needed whenever the API moved between the versions.

Compare with `ResultsComparer` (dotnet/performance) or a small script over BDN's JSON export.

## Batch 4 — DB counter comparison (Tier B, the valuable one)

**Files:** `src/benchmarks/Warp.ServerBenchmarks/Lab/LoadLab.cs` (add `--json <path>`),
`src/benchmarks/Warp.ServerBenchmarks/Lab/PerfCompare.cs` (new), `.github/workflows/perf.yml`.

The comparator is a `compare` subcommand on the existing lab binary, not a standalone script. It shares
the `ArmResult` record with the code that writes the JSON, so a field rename breaks the build instead of
silently producing a wrong comparison in CI; it is covered by the same analyzers as everything else; and
CI already has the published lab, so no extra toolchain is installed to run it.

This is the tier that would have caught the real 7.1.0 findings, because it measures what the release
notes claim. The lab already computes everything needed; it only lacks machine-readable output.

1. `--json` writes the per-arm medians (statements/job, rows, buffers, and the timings marked
   advisory) plus the arm's full parameters.
2. The workflow, on one runner, in one job:
   - Postgres as a service container, **pinned by digest**, fixed `shared_buffers`/`max_connections`.
   - Build `head` and `base` variants — base being today's harness with the changed runtime files
     taken from the merge-base (the swap described above).
   - Run arms **interleaved**, fresh database per run, `--warmup=1`.
   - `perf-compare.py` diffs the JSON and writes a markdown table to `$GITHUB_STEP_SUMMARY`.
3. Gate: fail on statements/job or rows regressing beyond tolerance; buffers warn; timings display only.

**PR budget:** two or three short arms (baseline, mutex-8, dispatcher-4KB) at 10,000 jobs, roughly
10 minutes. The 120,000-job claim arm and the full concurrency sweep are **nightly**, not PR — they
take ~30 minutes and their value is trend, not gating.

## Batch 5 — Nightly depth

**Files:** `.github/workflows/perf-nightly.yml`, `.github/workflows/mutation.yml` (new).

- The long arms from Batch 4, against the previous release tag rather than the merge-base, so the
  trend line is "what has the release drifted", published to the step summary.
- **Stryker mutation testing** already exists (`src/tests/Warp.Tests.Mutation`, `STRYKER.md`) and runs
  nowhere. It is far too slow to gate a PR; schedule it and report the score.
- The two suites CI does not currently run at all: Playwright screenshots/e2e (`npm run screenshots`,
  `test:e2e:live`) and `Warp.PerfTest`'s idle query counts — the latter matters because 7.1.0
  probably invalidated its published tables and nobody would know.

## Batch 6 — Docs

`docs/perf-results.md` gains a short "how the numbers are produced now" section pointing at the
workflow, so a future session does not re-derive the method by hand. `website/docs/operations/benchmarks.md`
gets its stale "all 9 background tasks" corrected (there are 13 `IServerTask` implementations) and its
tables re-measured or dated.

## What this deliberately does not do

- **No time-based gate on the server arms.** Not because timing cannot be gated — Batch 3 gates it for
  microbenchmarks — but because a 30-second iteration cannot buy the sample size the effect requires.
  If throughput gating on the server arms is genuinely wanted, the honest route is a self-hosted runner
  on fixed hardware, where both the CV and the per-iteration cost drop enough to make it resolvable.
- **No historical baseline database.** Comparing against stored numbers from other VMs is the single
  most common way perf CI becomes noise. Every comparison here is same-runner, same-job.
- **No gate on buffers alone.** Cache state moves them a few percent; they are a strong *diagnostic*
  (the bimodal claim plan was identified by block counts, not by time) but a weak gate.

## Finish

Full matrix green, `perf.yml` green on a no-op PR (proving the gate does not fire on noise), and one
deliberately regressed branch (reintroduce `queue = ANY(...)`) proving it *does* fire.

## Parallelism

Batches 1 and 2 are independent of 3-5 and deliver most of the protection for the least machinery —
do them first and separately. Batch 3 and Batch 4 share only `perf.yml` and can otherwise proceed in
parallel.
