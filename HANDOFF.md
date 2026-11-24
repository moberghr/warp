# Handoff: PR #346 review fixes (perf/claim-path)

> Temporary. Delete this file (drop its commit) before the PR merges.

Written 2026-11-24 to continue the work on another machine. Read `CLAUDE.md` and `.claude/rules/` first.

## Ground rules the user set

- **Every fix gets a test that fails first.** Write the test, watch it fail on the current code, then fix. For a fix already written, confirm by reverting the source files (`git diff > patch`, `git checkout -- files`, run, `git apply patch`).
- **Perf numbers come from CI only.** Never run benchmarks or load labs locally. Tests run locally.
- **Run tests by class or namespace, not the full suite**: the full suite ran this machine out of memory. `dotnet test --project src/tests/Warp.Tests/Warp.Tests.csproj --no-build -- --filter-class "*Name*"` or `--filter-namespace "Warp.Tests.Worker"`. PR CI runs the full suite on both databases.
- **Commit per fix, push when a set is done.** The user asked for this branch to be pushed; do not force-push.
- **PR replies go through `work` only**: draft with `work pr reply <thread> "<text>"`, post with `work pr post <thread>...` and only after the user said yes to that draft. No `gh` comments, no resolving threads by hand. `work pr replies` lists the threads.
- Literal absolute paths in shell commands; no `cd dir && cmd`. No bare `git stash`.

## State of the PR

- All 14 perf-gate scenarios and all test suites were green at `ea12fc87`.
- Review: 24 threads from two reviewers (one bot). The user approved fixing all of them.
- Commit trailer used so far: `Co-Authored-By:` plus the `Claude-Session:` line of the session doing the work.

## Done (pushed)

| Commit | Threads | What |
|---|---|---|
| `8d6741d3` | `PRRT_kwDOIZTPCc67OW9P` (A1) | A transient `DbException` on the completion gets the deadlock retry budget, then the split. Test: `CompletionBatchTests.FlushAsync_TransientDbExceptionOnTheFirstAttempt_CompletesEveryJob`. |
| `493a1586` | `...67OW9W`, `...67OW9X` (A3), `...67OGb3`, part of `...67OGcN` | Job monitor: a failed save puts rows back; cancellations applied even if the save fails; cancel not awaited; a tick takes rows under the entry's lock and only its own job's "Processing" row. Tests: `RunningJobMonitorFailureTests` (4 tests, both databases). |
| `5fd31e55` | `...67OW9a`, `...67OW9c` (A4) | SQL Server listener: sweep on a wall-clock interval (`SweepInterval`, 1 min); each listener holds a session applock named after its queue, and a peer drops a queue only when `APPLOCK_TEST` says it is free. Tests in `SqlServerNotificationTransportTests`. |
| `71a48664` | `...67OW9p` (B5), `...67OGc8`, part of `...67OW-F` | Signal hold only for tasks with signals; hold subtracted from the following wait; hold on the loop's `TimeProvider`. Tests in `ServerTaskLoopBookkeepingTests`. |

Their drafted replies exist in `work` on the first machine; the texts are at the end of this file. Add the commit SHA to each before posting.

## Still to do

Each item: the thread, the planned change, and the failing test to write first.

1. **B2: completion and keep-alive match on id only** (`...67OW9h`, rest of `...67OGcN`).
   - Guard both statements, on both providers: update a row only while `CurrentState = Processing` AND it is still this run's claim: `CurrentWorkerId = worker` OR (`CurrentWorkerId = claimedBy` AND `LastKeepAlive = claimedKeepAlive`). A re-claim by the same group stamps a new keep-alive, so the stale run cannot match.
   - `PendingCompletion` must carry the worker id, the claimer id (group id in dispatcher mode) and the claim's keep-alive, captured before `BuildFinalization` clears them. `RunningJobMonitor.Register` needs the same for the renewal. `IWarpSqlQueries.CompleteJobsAsync` / `RenewRunningJobsAsync` signatures change (both are new in this PR).
   - A completion that loses the race writes nothing, counts nothing, is logged. Single-worker mode finalizes through EF and stays as on `main`.
   - Tests: a requeued and re-claimed job is not completed by the old run; a stale renewal does not stamp the new owner. `WorkerPathLockstepTests`, `CompletionBatchTests` and `CompletionPlanStabilityTests` build completions and will need the claim values.
2. **Flusher shutdown** (`...67OGbb`): `CompletionFlusher.StopAsync` waits on the loop and final flush only until the shutdown token; writes and announcements run on a flusher token cancelled at that point. Test: a hung write does not hold `StopAsync` past its token (reuse `HoldFirstFinalizedTransport` in `BatchedCompletionIntegrationTests`).
3. **Flusher buffer** (`...67OW9m`, B3/B4):
   - An `Add` after `StopAsync` writes that completion inline.
   - Each flush is written in statements of at most `4 x CompletionBatchSize` jobs (the user agreed to 4x: splitting at 1x would add statements under load).
   - Fix the `CompletionBatchSize` docs in `Configuration.cs` ("each worker buffers", "set to 1 to disable batching" are no longer true).
   - **Open decision, not approved:** making a worker wait when more than `2 x CompletionBatchSize` completions are unwritten. Leave it out unless the user says yes.
4. **B10: partial-index filters** (`...67OW9v`, `...67OGa_`): call `WarpPartialIndexes.Apply` again after the `EntityConfigurators` loop in `WarpModelCustomizer` and `WarpServerContext` (as the storage re-pin does). Add to the startup model guard (`WarpModelGuard`) a check that every Warp index filter names an existing column, with a message saying to call `ApplyWarpModel` after renaming. Tests: a configurator that renames `Job.CurrentState` produces a filter with the new name; a rename after `ApplyWarpModel` fails the guard.
5. **B1: SQL Server compatibility level** (`...67OW9e`): fail-fast check at startup for level >= 130 (`OPENJSON`), with the `ALTER DATABASE` statement in the message. Release note and SQL Server setup docs. Test: startup against a database set to level 120.
6. **B7: `SET LOCAL` warning** (`...67OW9q`): capture Npgsql notices (`NpgsqlConnection.Notice`) around a claim. If PostgreSQL warns that `SET LOCAL` is outside a transaction block, switch `ClaimPlanGuard` to `SELECT set_config('enable_sort', 'off', true);` (check the claim's result-set handling still works with the extra statement). Either way `ClaimPlanStabilityTests` should assert the claim raises no notice.
7. **B8: resolver constructors** (`...67OW90`): restore the public constructors on `RateLimitResolver(IRateLimitManager)` and `ConcurrencyLimitResolver(IConcurrencyLimitManager)`; registration uses a host's own manager when one is registered and the `DeferredWarpContext` path only for Warp's own. Update rule 8.8.1. Tests: direct construction works; a custom manager is consulted.
8. **B9: unsaved-staged-jobs warning** (`...67OW96`): no warning for an attempt whose handler threw. Release note: a handler's own unsaved entities are lost silently. Make these save what they stage: the demo `OrderProcessingCommand.cs`, `metadata.md`, `tracing.md`, `reliability.md`, and the saga `NotFoundAsync` path.
9. **Low: diagnostics dictionary leak** (`...67OW9-`): `DispatcherDiagnostics.ClaimedAt` entries are added when the channel-wait meter is on but removed only when the worker-busy meter is on. Remove on pickup whenever channel-wait is enabled. Test with only that meter subscribed.
10. **Low: generated dispatcher, synchronous throw** (`...67OW-C`): in `WarpMediatorGenerator`, the innermost delegate `(req, ct) => AsUnit(handler.HandleAsync(req, ct))` should catch a synchronous throw and return `Task.FromException<Unit>(e)`, as the reflection path's async delegate does. No extra stack frame (see rule 8.10 on failure stack traces). Test: a behaviour that attaches a continuation without awaiting sees a faulted task.
11. **Tests** (`...67OW-F`, `...67OW-K`, `...67OGdQ`, `...67OGcq`):
    - One end-to-end test (a continuation completing) with the production `ServerTaskSignalInterval` of 500 ms; `WarpTestServer` sets it to zero for everything else.
    - Put `DispatcherDiagnosticsTests` and the two-host test in `DatabasePushIntegrationTests` in the `HeavyIntegration` collection; tag diagnostic measurements with the worker group id and count only the test's own.
    - `CompletionPlanStabilityTests`: capture only commands on the test context's connection, and dispose the capture before the replay.
    - `AllocationSampler` (benchmarks): keep and dispose the per-listener subscriptions.
12. **Rules docs**: update `.claude/rules/performance.md` (6.13, 6.14, 6.18) and `architecture.md` (2.9) for the fixes above and the four already done.
13. **PR description**: add release notes for the SQL Server compatibility level and the unsaved-entities note.
14. After pushing: update each drafted reply with its commit SHA, show the user, post with `work pr post` once approved. Then rerun the PR perf gate (automatic on push) and the matrix (push an empty commit to `bench/perf-matrix`; it measures the current head of this branch).

## Next task after the review: clock skew (required before 8.0)

Warp servers stamp and compare times with their own clocks, so one server with a wrong clock breaks the cluster: `StaleJobRecovery` requeues healthy servers' running jobs (duplicate executions), scheduled jobs and retries run early, the recurring scheduler skips firings, `ServerCleanup` evicts healthy servers, `ExpirationCleanup` deletes early. There is no skew detection today.

Direction agreed so far (not designed in detail): take "now" from the database (`now()` / `SYSUTCDATETIME()`) in the statements that compare across servers (keep-alive, stale-job recovery, heartbeat, server cleanup, scheduled activation, leases), and have the heartbeat compare the server's clock with the database's and warn on drift. Make it its own PR.

## Other loose ends

- Temporary branches on the remote, safe to delete once no longer needed: `bisect/sqlserver-dispatcher`, `candidate/sqlserver-completion-recompile`, `candidate/sqlserver-completion-loopjoin`, `candidate/sqlserver-completion-joinorder`. `bench/perf-matrix` holds the matrix harness (`src/benchmarks/Warp.MatrixBench`, `.github/workflows/perf-matrix.yml`).
- Latest matrix run on `ea12fc87`: https://github.com/moberghr/warp/actions/runs/45339639284 (36 cells, every correctness check passed).
- Watch items from the perf gate, not yet looked at: message routing on PostgreSQL reads more buffers per job (356 -> 613); publish "one save for 1,000" dipped 15% with wide error bars. Bisect runs for both were dispatched on `bisect/sqlserver-dispatcher` (11 runs, around 06:15 UTC on 24 Nov) and never read. Idle SQL Server buffers are being handled in another session.

## Drafted replies (add SHAs; post only after the user approves)

- `...67OW9P`: Fixed: a transient DbException (command timeout, dropped connection) now gets the same bounded retry with backoff as a deadlock, and once the retries are spent the range is split like any other failure instead of escaping to the flusher. Test: an injected transient failure on the first attempt still completes every job, with its counters.
- `...67OW9W`: Fixed: the tick no longer awaits the cancel. It starts it and moves on (failures are logged from a continuation), so a handler whose cancellation callback blocks holds only its own job. Test: a job whose cancellation callback blocks does not stop its group from being renewed.
- `...67OW9X`: Fixed: if the save fails, the taken Processing rows and drained logs are put back on their entries, so the next tick or the job's finalization writes them; only a job that already finished loses them, and that is logged. The cancellations the renewal returned are applied whether or not the save succeeds.
- `...67OGb3`: Fixed together with the comment on line 181.
- `...67OGcN`: Fixed: an entry is checked as released under its own lock when the tick takes its rows, and the Processing row is taken only for that entry's own job id. The keep-alive statement also matches the claimer, so a late renewal cannot stamp a job another worker has since claimed. (Second half pending: item 1.)
- `...67OW9h`: Fixed: the completion statement on both providers updates a job only while it is still Processing and still owned by the claimer. A completion that loses that race writes nothing, counts nothing and is logged. Single-worker mode finalizes through EF as on main and is unchanged. (Pending: item 1.)
- `...67OGbb`: Fixed: StopAsync waits for the loop and the final flush only until the shutdown token fires; a write cancelled at the deadline leaves its jobs Processing for StaleJobRecovery. (Pending: item 2.)
- `...67OW9m`: (1) An Add after StopAsync writes that completion inline. (2) Each flush is written in statements of at most 4 x CompletionBatchSize jobs, and the docs describe the per-group buffer. Not done: a worker wait when completions pile up, which would couple workers to flush latency again. (Pending: item 3; reword once the decision is made.)
- `...67OW9a`: Fixed: the sweep runs on a wall-clock interval (once a minute per listener), not every 10 loop iterations.
- `...67OW9c`: Fixed: each listener holds a session-scoped applock named after its queue on its listening connection, and a peer drops a queue only when that lock is free and its oldest message is older than StaleQueueAge. New test: a live listener's queue with an old message survives a peer's sweep. `message_enqueue_time` is documented as UTC; a test checks it against GETUTCDATE().
- `...67OW9e`: Fixed: startup check for compatibility level 130, release note and docs. (Pending: item 5.)
- `...67OW9q`: Checked with Npgsql notices captured; (state the outcome). (Pending: item 6.)
- `...67OW9v` / `...67OGa_`: Fixed: filters are rebuilt after the EntityConfigurators, and the startup guard catches a rename made after ApplyWarpModel. (Pending: item 4.)
- `...67OW90`: Fixed: public constructors restored; a host's own manager is used when registered. (Pending: item 7.)
- `...67OW96`: Fixed: no warning for an attempt whose handler threw; release note; demo, docs and the saga NotFoundAsync path save. (Pending: item 8.)
- `...67OW9p`: Fixed: the hold is applied only to loops whose task has signals, so Heartbeat, ScheduledActivation and the other timer-only tasks keep their configured cadence exactly.
- `...67OGc8`: Fixed: for signal-driven tasks the hold is subtracted from the timed wait that follows. Timer-only tasks get no hold at all.
- `...67OW9-`: Fixed: pickup removes the entry whenever the channel-wait meter is enabled. (Pending: item 9.)
- `...67OW-C`: Fixed: the innermost delegate returns a synchronous throw as a faulted task. (Pending: item 10.)
- `...67OW-F`: Fixed: the hold waits on the loop's TimeProvider and a fake-clock test tells a hold from no hold; one end-to-end test runs with the 500 ms default. (Second half pending: item 11.)
- `...67OW-K`, `...67OGcq`, `...67OGdQ`: see item 11.
