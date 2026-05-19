---
sidebar_position: 6
---

# Releases

## 0.15.2

*2026-05-18*

Two bug fixes — one correctness fix in `ExpirationCleanup`, one supervisor behavior change that stops silently dropping `BackgroundService` state writes. No public API changes.

### Fixed: `ExpirationCleanup` foreign-key violation when batch splits a parent from its children

`ExpirationCleanup.RunCleanupAsync` and `RunCountBasedCleanupAsync` selected expired jobs whose children were all expired and then `ExecuteDeleteAsync`-ed the batch in one statement. When `Take(ExpirationBatchSize)` (default 100) cut off mid-tree — parent included, some expired children outside the batch — the DELETE intermittently violated the self-FK `fk_job_job_parent_job_id`:

```
23503: update or delete on table "job" violates foreign key constraint
       "fk_job_job_parent_job_id" on table "job"
```

The fix tightens both predicates to delete only **leaves** (`!x.ChildJobs.Any()`). An expired job with any child rows still pointing at it stays in place; on the next cleanup tick its (already-expired) children are gone and the parent itself becomes a leaf and is deleted. Trees of expired jobs drain one level per tick — a 3-level chain takes 3 ticks of `ExpirationCleanupInterval` (default 5 minutes) to fully clear, but never violates the FK.

Behavior preserved:
- Failed jobs still never auto-expire (`ExpireAt = null`, §8.2). An expired ancestor of a `Failed` descendant remains uncleanable until an operator requeues/deletes the failed leaf — same as before.
- Sibling successes still expire and clean up independently of failed siblings — also the same as before. Trace context for failure investigations should come from your OTel export, not the operational DB.

No action required on upgrade.

### Changed: supervisor no longer silently swallows DB write failures

`BackgroundServiceSupervisor` previously caught and logged any DB exception from its own state writes (`SetStatus`, `RecordFault`, `ResetRestartCount`) and continued as if the write had succeeded. Under DB contention (pool exhaustion, command-timeout under load) this meant the dashboard timeline could quietly stop matching reality — the service would still run, but the row's `Status` and the lifecycle log would diverge from the actual supervisor state. Observers never fired for the dropped transition, so anyone watching `IBackgroundServiceStatusObserver` would just see gaps.

The supervisor now lets those exceptions propagate to a single outer iteration-level `try/catch` that treats the failure exactly like a faulted service: emits `LogSupervisorFault` (a new `Lifecycle / Error` log entry distinct from `LogFaulted`, which is reserved for user-code faults), increments `warp.background_services.faulted` (tagged with `exception_type`), waits out the next entry in the backoff sequence on real wall-clock time, and re-runs the iteration. `RestartCount` is **not** incremented for supervisor-side faults — that counter remains reserved for user-code faults.

The trade-off this codifies: service execution is now gated on the supervisor's ability to persist its status. A `BackgroundService` whose own `ExecuteAsync` doesn't touch the DB at all will be briefly paused (1s+ backoff) during a Warp-DB blip rather than running with a stale dashboard reading. This was judged the better default — silent staleness on a thrashing service is worse to diagnose than a delayed-but-visible fault.

Operator-facing changes:
- Lifecycle log now has a new entry type `Supervisor faulted: <ExceptionType>: <message>` (Lifecycle / Error) — distinct from `Service faulted: …` which is still emitted for user-code throws.
- `warp.background_services.faulted` counter is now incremented for supervisor-side faults too. If you alert on this counter, expect a small bump under DB contention; the `exception_type` tag will distinguish infrastructure faults (`NpgsqlException`, `SqlException`) from user faults.
- For deployments using `WarpBackgroundService` with no scoped DB dependencies, expect occasional brief restart-backoff cycles during DB pressure rather than silent execution.

No public API changes.

## 0.15.1

*2026-05-18*

One packaging fix to `Moberg.Warp.Core`. No public API changes.

### Fixed: source generator missing from the NuGet package

The mediator/job dispatch source generator (`Warp.SourceGenerator`) was wired up as a project-level analyzer in `Warp.Core.csproj` but never packed into `Moberg.Warp.Core.nupkg`. In-tree consumers (Warp's own tests, demos, benchmarks) reference Core via `<ProjectReference>` so the analyzer flowed through normally — masking the gap. Downstream NuGet consumers got the runtime DLL but no generator, so `[ModuleInitializer]`-driven `WarpGeneratedHandlerRegistry` registrations never ran. `AddWarpWorker` then registered nothing and the first `IMediator.Send(...)` threw `"No handler registered for {RequestType}"`.

The bug has been present since handler auto-registration shipped in 0.10.0 (2026-04-27). It surfaced for consumers with more than a trivial number of handlers, where the manual `services.AddScoped<IRequestHandler<,>, ...>()` workaround stopped scaling.

The fix is one block in `Warp.Core.csproj` mirroring what `Warp.Http.csproj` has had since day one — embed the generator DLL into `analyzers/dotnet/cs/`:

```xml
<ItemGroup>
  <None Include="..\Warp.SourceGenerator\bin\$(Configuration)\netstandard2.0\Warp.SourceGenerator.dll"
        Pack="true" PackagePath="analyzers/dotnet/cs/" Visible="false" />
</ItemGroup>
```

No action required on upgrade beyond bumping the `Moberg.Warp.Core` version. Consumers that worked around the bug with reflection-based handler scanning can drop the workaround once on 0.15.1 — `AddWarpWorker` / `AddWarp` will pick handlers up automatically.

## 0.15.0

*2026-05-17*

One new opt-in addon — `WarpBackgroundService` — dashboard-visible analog of .NET's `BackgroundService` that runs outside the worker pool. No breaking changes; no public API removals.

### New: `WarpBackgroundService` addon

Opt-in via `opt.AddBackgroundService<T>()`. Lets you register a long-lived background loop that Warp manages — restart-with-backoff, optional cluster-singleton coordination via lease, captured `ILogger<T>` output in the dashboard. Migration from plain `BackgroundService` is one line — replace the base class.

```csharp
public sealed class KafkaDrainService : WarpBackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<KafkaDrainService> _logger;

    public KafkaDrainService(IServiceScopeFactory scopes, ILogger<KafkaDrainService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public override ServiceScope Scope => ServiceScope.Singleton;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // ... drain a batch, commit offsets, etc.
            _logger.LogInformation("Drained {Count} messages", count);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
}

services.AddWarpWorker<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.AddBackgroundService<KafkaDrainService>();
});
```

**Scope:**

- `ServiceScope.PerServer` (default) — every server with the service compiled in runs an independent instance. Matches plain `BackgroundService` semantics.
- `ServiceScope.Singleton` — exactly one instance across the cluster, coordinated by a lease in `background_service_lease`. The lease has a 30 s TTL, renewed every 3 s on the existing `Heartbeat` server task. On hard-kill, failover takes ≤ 30 s; on graceful shutdown, the holder deletes the lease row before exit so a waiter acquires immediately.

**Lifecycle is always-on:** if `ExecuteAsync` throws — or returns without the cancellation token being signalled — the supervisor records the fault, applies exponential backoff (1 → 2 → 4 → 8 → 16 → 30 s, capped), and re-invokes. A successful run of ≥ 5 minutes resets the counter. There is no stop button in v1 — to disable a service you remove the registration and redeploy.

**Log capture out of the box:** a per-instance `ILoggerProvider` is wired up automatically; user `ILogger<TService>` calls flow into both the normal log stack AND a `background_service_log` table that the dashboard renders. Volume guardrails: level filter (`MinLogLevel` defaults to `Information`, override per-service), 100 entries/sec rate cap with a 10 s drop window, 4 KB message truncation, retention of 1 000 rows per instance / 7 days (both global-configurable). Lifecycle events (`Started`, `LeaseAcquired`, `LeaseLost`, `Faulted`, `Restarting`, `Stopped`, `ConfigurationMismatch`) share the same table with `Source = Lifecycle`.

**Configuration-mismatch detection:** each server stamps its declared `Scope` onto its instance row. If a rolling deploy ships a version where `Scope` changed (e.g., `PerServer → Singleton`), the new instances detect the disagreement against the existing `background_service_definition` row, write `Status = ConfigurationMismatch`, and refuse to run user code until the deploy completes. The dashboard surfaces the mismatch loudly so split-brain across the transition window is impossible.

**Dashboard:** new `/warp/services` nav, gated by the existing `/api/addons` discovery flag (`services: true`). List view aggregates per service name; detail view shows per-instance tabs with status, captured exception, lease panel (singleton only) with TTL countdown, and a log tail filterable by source and level.

**Lifetime contract:** registered as singleton. Inject `IServiceScopeFactory` and create scopes per work unit — same pattern recommended for plain `BackgroundService`. `ValidateScopes = true` in Development catches the captive-scoped-dependency foot-gun (e.g., taking `DbContext` directly in the ctor) at startup.

**Telemetry:** four OTel counters — `warp.background_services.started`, `warp.background_services.faulted` (tagged `service_name` + `exception_type`), `warp.background_services.restarts`, `warp.background_services.lease_lost`.

### Schema change

If you manage your Warp migrations manually, opt-in users will need to add four new tables and two indexes (note the second index is the one for the cross-server dashboard log tail — easy to miss):

```sql
CREATE TABLE warp.background_service_definition (...);
CREATE TABLE warp.background_service_instance   (...);
CREATE TABLE warp.background_service_lease      (...);
CREATE TABLE warp.background_service_log        (...);

CREATE INDEX ix_background_service_log_per_instance
    ON warp.background_service_log (server_id, service_name, id);
CREATE INDEX ix_background_service_log_by_service
    ON warp.background_service_log (service_name, id DESC);
```

EF Core auto-migration produces these from the model when `opt.AddBackgroundService<T>()` triggers the entity configurators. Deployments that do **not** call `AddBackgroundService<T>()` see no schema change — the entities are absent from the model and the cleanup tasks (`ExpirationCleanup`, `ServerCleanup`, `WarpServerRegistration.StopAsync`) guard with `Model.FindEntityType(...) != null` before touching them.

### Stats

- ~9 500 lines of source + tests across 106 files
- 1 931 tests (PostgreSQL + SQL Server) — zero skipped, zero failures
- Full feature documentation at `features/background-services.md`

### Links

- [GitHub Release](https://github.com/moberghr/warp/releases/tag/0.15.0)

---

## 0.14.1

*2026-05-16*

Two patch-level fixes to the dashboard surface. No public API changes.

### Fixed: dashboard timestamps off by the client's UTC offset

The dashboard rendered `Started`, `Heartbeat`, and other server/job timestamps in the client's local timezone but advertised them as the UTC moment — a server that started 5 minutes ago appeared as "about 2 hours ago" on a `UTC+2` client. The root cause was on the backend: SQL Server `datetime2` (and Postgres `timestamp` without timezone) columns carry no `DateTimeKind` info, so EF Core materialized values with `Kind=Unspecified`. `System.Text.Json` then serialized the resulting `DateTime` without a `Z` suffix, and the browser's `new Date(...)` parsed the unmarked string as local time.

A new `ValueConverter` applied via `WarpModelCustomizer` stamps `Kind=Utc` on every `DateTime` / `DateTime?` property read from `Warp.Core`-assembly entities. The convention is assembly-scoped (not namespace-prefixed), so a user's entity sharing the same DbContext is never touched. Any property the user has already attached their own converter to is left alone.

No action required on upgrade — the fix takes effect after a redeploy. Production code that already wrote `Kind=Utc` (everything routed through `TimeProvider.GetUtcNow().UtcDateTime` per [§5.7](https://moberghr.github.io/warp/docs/) and Warp itself) continues to behave identically.

### Changed: addon-discovery probes consolidated into `/api/addons`

The dashboard previously discovered opt-in addons by firing one `GET` against each addon's data route (`/api/concurrency`, `/api/ratelimits`, `/api/dashboard/push/probe`) and treating the 404 as the "addon not enabled" signal. That worked, but it surfaced three red 404s in DevTools every session and made the network tab noisy when triaging unrelated issues.

`GET /api/addons` replaces all three with one always-200 response:

```json
{ "concurrency": true, "rateLimits": false, "push": true, "sagas": false }
```

The booleans are computed from DI service presence. The dashboard makes one round-trip per session, decides nav visibility from the flags, and uses `push` to gate the SignalR connect. A one-shot retry on transient failure keeps a single network blip from hiding all addon nav for the rest of the session.

The legacy `/api/dashboard/push/probe` route was removed in this release. `/api/concurrency` and `/api/ratelimits` still 404 when the corresponding addon isn't registered — they're the data endpoints — but the bundled dashboard no longer probes them speculatively.

External integrations that were polling `/api/dashboard/push/probe` should switch to `/api/addons` and read the `push` boolean.

## 0.14.0

*2026-05-14*

Two new addons (`[Timeout]` and `[RateLimit]`), realtime dashboard updates over SignalR, opt-in handler-reported progress bars, and a major idle-query-rate reduction on the server-task path. One small but pointed PG provider fix (honour `NpgsqlDataSource`) makes Aspire / Managed Identity / Vault setups work without manual wiring. Two breaking surfaces — a server-task default flip and a cross-addon `IXxxMetadata` property rename — fall out of this work; both are spelled out below.

### New: `[Timeout]` addon

Opt-in via `opt.AddTimeout()`. Caps how long a handler is allowed to run; on deadline, the worker cancels the handler's `CancellationToken`.

```csharp
opt.AddRetry();    // optional, but MUST come before AddTimeout
opt.AddTimeout(o => { o.Default = TimeSpan.FromMinutes(10); });

[Timeout(seconds: 30)]                                            // Delete, PerAttempt
[Timeout(seconds: 30, Mode = TimeoutMode.Fail)]                   // throws TimeoutException → retried by AddRetry
[Timeout(seconds: 30, Mode = TimeoutMode.Fail, Scope = TimeoutScope.Total)]   // bounds the entire retry chain
public class CallSlowApi : IJob { }

// or per-publish
await publisher.Enqueue(new GenerateReport(),
    new JobParameters().WithTimeout(TimeSpan.FromMinutes(5)));
```

Two modes (`TimeoutMode`):

- **`Delete`** (default) — pipeline sets `Outcome { State = Deleted }`. **Not** retried by `AddRetry` (the outcome path bypasses retry's `catch`). Use when "kill it and move on" is the right answer.
- **`Fail`** — pipeline throws `TimeoutException`, which `AddRetry` catches and reschedules. Without `AddRetry`, the job ends `Failed`. Use when a slow upstream may succeed on retry.

Two scopes (`TimeoutScope`):

- **`PerAttempt`** (default) — each attempt gets a fresh budget.
- **`Total`** — `DeadlineUtc` stamped on publish; once past the deadline, every attempt's timer fires immediately. Bounds total wall-clock to roughly `TimeoutSeconds` plus retry backoff. Only useful with `Mode = Fail`.

Cooperative cancellation only — handlers that ignore the token complete normally, and the timeout doesn't fire after-the-fact. See [the Timeout feature page](features/timeout) for the full breakdown.

**Pipeline ordering rule:** `AddRetry()` MUST be called before `AddTimeout()`. DI insertion order is outer→inner; retry has to wrap timeout to see the `TimeoutException`. `TimeoutAddonOrderingTests` pins this.

### New: `[RateLimit]` addon

Opt-in via `opt.AddRateLimit()`. Throttle jobs sharing a key to N starts per window.

```csharp
opt.AddConcurrency();  // optional, but MUST come before AddRateLimit
opt.AddRateLimit();

[RateLimit("sendgrid", count: 10, perSeconds: 60)]                              // Fixed, Skip (defaults)
[RateLimit("sendgrid", count: 10, perSeconds: 60, Mode = RateLimitMode.Wait)]   // requeue surplus
[RateLimit("crm", count: 100, perSeconds: 60, Style = RateLimitStyle.Sliding)]  // rolling window
public class SendEmail : IJob { }

// or per-publish
new JobParameters().WithRateLimit("sendgrid", 10, TimeSpan.FromSeconds(60));
```

Two styles (`RateLimitStyle`):

- **`Fixed`** (default) — wall-clock window floor-aligned to global UTC ticks. Cheap, predictable bursts at boundaries.
- **`Sliding`** — rolling window over the last N starts, defensively trimmed. Smoother distribution; slightly more storage churn per check.

Two outcome policies (`RateLimitMode`):

- **`Skip`** (default) — surplus jobs end `Deleted`.
- **`Wait`** — surplus jobs are rescheduled via `JobOutcome.RescheduledState` with 100–500 ms jitter on lock contention.

Live state lives in a new `RateLimitBucket` entity; admin overrides in `RateLimitOverride`. Both are contributed only when `AddRateLimit()` is registered. `perSeconds` is capped at 7 days (`MaxWindowSeconds`). The pipeline lock is released after check-and-increment — **not** held during handler execution (unlike `[Mutex]`).

Dashboard CRUD lives at `/warp/ratelimits` and follows the hide-on-404 nav probe.

**Caveats:**

- DB push does **not** accelerate `Wait`-mode reschedules — they land in `State.Scheduled` and depend on `ScheduledJobActivation` polling.
- Don't put PII in the key — keys appear in `JobLog.Message` and on the dashboard.
- **Pipeline ordering rule:** `AddConcurrency()` before `AddRateLimit()`. Mutex/Semaphore rejection should not waste a rate-limit token; reversing the order causes the wasted-token bug until the next window rollover.

See [the Rate Limit feature page](features/rate-limit) for the matrix and tuning notes.

### Breaking: cross-addon `IXxxMetadata` property rename

`IConcurrencyMetadata` properties were unprefixed (`Key`, `Limit`, `Mode`) in 0.13.0. All `IXxxMetadata` interfaces share a single backing `Dictionary<string, object>` per job, so bare names silently collided when a job carried both `[Mutex]` and the new `[RateLimit]`. The fix renames every metadata property to its addon namespace:

- `IConcurrencyMetadata.Key` → `ConcurrencyKey`
- `IConcurrencyMetadata.Limit` → `ConcurrencyLimit`
- `IConcurrencyMetadata.Mode` → `ConcurrencyMode`
- `IRateLimitMetadata` ships with `RateLimitKey` / `RateLimitCount` / `RateLimitWindowSeconds` / `RateLimitMode` / `RateLimitStyle`.

The attribute and fluent surfaces (`[Mutex("k")]`, `[Semaphore("k", N)]`, `WithMutex(...)`, `WithSemaphore(...)`) are unchanged — only direct callers of `IConcurrencyMetadata` need updating. Custom pipeline behaviours, test fakes, or third-party integrations that read or set these properties must rename to the prefixed form.

### New: realtime dashboard push (SignalR)

Opt-in via `opt.AddDashboardPush()`. Registers a `WarpDashboardHub` at `${RoutePrefix}/api/hub` plus a `DashboardBroadcaster<TContext>` `BackgroundService`. The broadcaster subscribes to `ServerTaskSignals<TContext>` (third consumer after `Orchestrator` and `MessageRouter`) and emits `JobFinalized` / `MessageEnqueued` events to connected dashboards. Each broadcast carries the current `DashboardStatistics` DTO as the SignalR payload, so N connected clients no longer trigger N × `GET /api/status` refetches.

```csharp
builder.Services.AddWarp<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.UseDatabasePush();    // required for multi-server fanout
    opt.AddDashboardPush();
});
```

Coalesce window defaults to 100 ms (`WarpDashboardPushConfiguration.CoalesceWindow`) — burst signals (a 50-job batch finalising) collapse to one broadcast per kind per window.

**Caveats:**

- Multi-server fanout reuses `UseDatabasePush()`. Without it, each server's broadcaster only sees signals from its own workers; clients connected to server A miss events originating on server B until the 30 s safety-net poll.
- Per-view data (filtered job lists, job detail, logs) stays on event-driven REST refetch. Push is invalidations + the stats DTO, not per-view payloads.
- Frontend probes `${RoutePrefix}/api/dashboard/push/probe` once at boot and falls back to 30 s polling when the addon is absent (hide-on-404 mirroring `/api/concurrency`).

Auth piggybacks on the existing `WarpUIMiddleware` — both the SignalR negotiate and the WebSocket-upgrade HTTP requests pass through `/api/`, so an auth-protected dashboard requires no extra wiring.

See [the Dashboard Push feature page](features/dashboard-push) for telemetry hooks and tuning.

### Breaking: `ServerTaskSignals<TContext>` moved namespace

`ServerTaskSignals<TContext>` and `ServerTaskSignal` enum moved from `Warp.Worker.Services` to `Warp.Core.Events` so addons can subscribe without taking a dependency on the worker assembly. `Subscribe()` is promoted to `public IDisposable`. Anyone reaching into these types directly (rare — most consumers use the addon surface) needs to update their `using` directive.

### New: `IJobContext.ReportProgress(name, percent)`

```csharp
public class GenerateReport : IJobHandler<GenerateReportRequest>
{
    private readonly IJobContext _context;

    public GenerateReport(IJobContext context) => _context = context;

    public async Task HandleAsync(GenerateReportRequest message, CancellationToken ct)
    {
        for (var i = 0; i < total; i++)
        {
            // ... process row i ...
            _context.ReportProgress("rows", (i * 100) / total);
        }

        _context.ReportProgress("upload", 0);
        await UploadAsync(ct);
        _context.ReportProgress("upload", 100);
    }
}
```

Percent is clamped to `0..100`. Multiple named bars per job are supported — pass an empty name (or use the `ReportProgress(int percent)` overload) for the single-bar case. The detail page renders one bar per name in the right column above History/Logs; the card is hidden entirely when a job reported no progress. Reporting is **opt-in per handler** — jobs that don't call `ReportProgress` incur zero overhead and produce no rows.

`IJobContext.ReportProgress` writes to an in-memory `JobProgressCollector` (one per running job, mirrors the existing `JobLogCollector`). The worker's existing `RunJobMonitor` loop drains the collector every ~1 s during handler execution and on the terminal commit. Each *changed* bar emits one row; unchanged bars emit nothing, so a stalled bar at 47% doesn't churn rows every second. Final values land in the same `SaveChangesAsync` as the terminal `JobLog` row.

Progress is **display telemetry, not state** — it never participates in the state machine, the orchestrator, or worker scheduling.

### Breaking: `IJobContext` gained two abstract members

```csharp
void ReportProgress(string name, int percent);
void ReportProgress(int percent);
```

Custom `IJobContext` implementations (test fakes, third-party pipeline integrations) need to add these two members. Both can be empty bodies if the fake doesn't care about progress. The concrete `JobContext` shipped in `Warp.Core` implements them via an internal collector reference.

### Perf: server-task idle query rate down ~92%

With `UseDispatcher = true` + `UseDatabasePush()` and default intervals, idle query rate drops from ~10–15 q/s to ~1.2 q/s — measured by `Warp.PerfTest --mode idle` against PG with an `ActivityListener` on the Npgsql source. See [perf-results.md](https://github.com/moberghr/warp/blob/main/docs/perf-results.md#idle-queries-per-second-server-task-overhead) for the full matrix.

### Breaking: server-task default changes

The query-rate work changes three defaults. Most users won't notice — the bookkeeping these defaults gate is internal — but anyone tuning intervals or implementing custom `IServerTask`s should review:

- **`IServerTask.LocksWithTransaction` defaults to `true`.** Server tasks now serialize via xact-scoped advisory locks (`pg_try_advisory_xact_lock` / `sp_getapplock` with `@LockOwner='Transaction'`) — auto-released on commit/rollback rather than held via a session-scoped Medallion lock. Cuts the per-iteration lock chatter from 3 round-trips (acquire/release + work) to 1 fold. Custom server tasks that need the **session-scoped** Medallion behaviour (e.g., tasks that span multiple transactions, or call `SaveChangesAsync` more than once per `ExecuteAsync`) must opt out by overriding `LocksWithTransaction => false`. `MessageRouter` does this — it commits once per routed message.
- **`CounterAggregationInterval` defaults to `60s`** (was `5s`). Counter rows are still written immediately by hot paths; only the aggregation roll-up cadence is relaxed. Dashboard `Statistic` cards will be at most 1 min stale instead of 5 s. If you rely on fresh aggregated counters, set it back: `opt.CounterAggregationInterval = TimeSpan.FromSeconds(5);`.
- **`MaxPollingInterval` auto-bumps to 5 min when `UseDatabasePush()` is called and the value is still at its default.** Push notifications cover work activation; the long polling fallback exists only to backstop missed notifications, which the listener already drains on reconnect. Explicit overrides on `opt.MaxPollingInterval` are respected — the auto-bump only fires when the value is still the class default. `MessageRoutingInterval` and `OrchestrationInterval` follow the same pattern.

### New: bounded server-task batching

Orchestrator, MessageRouter, ScheduledJobActivation, and StaleJobRecovery now bound their per-iteration work via `WarpWorkerConfiguration.ServerTaskBatchSize` (default `100`). This prevents a single iteration from churning through a multi-thousand-row backlog while holding the orchestration lock; subsequent iterations drain the remainder via `RerunImmediately = true`. Tune up if you've sized your DB to swallow larger batches.

### Fix: PG provider honours `NpgsqlDataSource`

`Warp.Provider.PostgreSql` previously opened raw `NpgsqlConnection`s from the EF connection string for its distributed lock, semaphore, and LISTEN/NOTIFY transport. That bypassed any `NpgsqlDataSource` attached to the `DbContext` options — Aspire's `AddAzureNpgsqlDataSource` against Postgres Flexible Server with Managed Identity, custom `NpgsqlDataSourceBuilder` config (Vault-issued passwords, custom cert validation, channel binding), and similar setups all broke with a `28000 / no pg_hba.conf entry` from the first `AddOrUpdateRecurringJob` call.

`UsePostgreSql<TContext>` now reads `NpgsqlOptionsExtension.DataSource` off the `DbContextOptions<TContext>` and threads it through to `PostgresLockProvider`, `PostgresSemaphoreProvider`, and `PostgresNotificationTransport` — lock + semaphore via Medallion's `PostgresDistributedSynchronizationProvider(DbDataSource)`, transport via `dataSource.OpenConnectionAsync`. When the data source isn't present (plain `UseNpgsql(connectionString)`), behaviour is unchanged.

No public-surface breaking changes — all new entry points are additive ctors plus a private resolver.

### Schema additions

Two new tables (contributed only when `AddRateLimit()` is registered):

- `RateLimitBucket` — live per-key window state
- `RateLimitOverride` — admin runtime overrides

Two new nullable columns on `JobLog` (always present):

- `Name` (`nvarchar(100)?` / `varchar(100)?`) — progress bar name; null for all non-`Progress` rows
- `Value` (`smallint?`) — percent `0..100`; null for all non-`Progress` rows

No new indexes — the existing `(JobId)` index serves the progress detail-page read; rate-limit reads are keyed by bucket name. Existing deployments need a one-step EF migration to add the two `JobLog` columns plus the two new tables (the latter only when opting into `AddRateLimit()`); no data backfill required.

### Dependencies

`@microsoft/signalr@^9.0.6` added to the UI for the new dashboard-push transport. Eleven transitive Dependabot alerts cleared via lockfile bumps (no direct upgrades).

## 0.13.0

*2026-05-11*

Concurrency-focused release: the Mutex addon generalizes into a unified Mutex + Semaphore primitive with a runtime-editable admin layer, OpenTelemetry coverage broadens to cover producer / receive / mediator / server-task spans, and `CompletionBatch` gets transient-deadlock retry. Mutex Wait mode is new. The OTel span rename and the addon namespace rename are both breaking.

### Breaking: Mutex addon renamed to Concurrency

The Mutex addon is generalized into a unified concurrency primitive that backs both `[Mutex]` (limit = 1) and the new `[Semaphore]` (limit > 1) over a single pipeline behavior and metadata contract. The rename is mechanical but breaking:

- Namespace `Warp.Core.Mutex` → `Warp.Core.Concurrency`
- `opt.AddMutex()` → `opt.AddConcurrency()`
- `MutexMode` → `ConcurrencyMode`
- `IMutexMetadata` → `IConcurrencyMetadata`
- Distributed lock-key prefix `warp:mutex:` → `warp:concurrency:`

`[Mutex("key")]` and `WithMutex("key", ...)` continue to work — they're now thin wrappers over the unified primitive with `limit = 1`. No database migration. The lock-key prefix flip means any in-flight locks held under the old prefix won't be observed by the new code at upgrade — locks are short-lived, but don't deploy a rolling restart mid-burst on the same hot key.

### New: `[Semaphore("key", N)]` attribute

The companion to `[Mutex]` for `limit > 1`. Same pipeline, same metadata interface, same admin layer — just exposes the limit. Default `Mode` is `Wait` (`[Mutex]` defaults to `Skip`).

```csharp
[Semaphore("sendgrid", 10)]
public class SendEmail : IJob { }
```

Backend implementations differ — documented on [the semaphore feature page](features/semaphore):

- **PostgreSQL** — N distinct named advisory locks (`warp:concurrency:k:0` … `warp:concurrency:k:{N-1}`) over `pg_try_advisory_lock`. Per-process slot cache with random start offset.
- **SQL Server** — delegates to Medallion's `SqlDistributedSemaphore`.

The two backends construct lock names differently at `limit = 1`, so `[Mutex("k")]` and `[Semaphore("k", N)]` against the same key are independent on PG but share the slot pool on SQL Server. **Pick one or the other per key** — mixing both attributes against the same key is a portability footgun. CLAUDE.md and the feature docs spell this out.

### New: Mutex Wait mode

`[Mutex]` now supports a `Mode` option. The existing default `Skip` short-circuits the duplicate to `Deleted`. The new `Wait` mode requeues the duplicate with `ScheduleTime = now` and writes a `Requeued` audit-log entry — mutual exclusion without dropping work.

```csharp
[Mutex("payment:123", Mode = ConcurrencyMode.Wait)]
public class ChargeOrder : IJob { }
```

Or fluent: `new JobParameters().WithMutex("payment:123", ConcurrencyMode.Wait)`.

Caveats are promoted to the top of [the mutex feature page](features/mutex):

- **Best-effort order across requeued jobs**, not strict FIFO. The wait set is not a queue; on lock release, whichever requeued copy a worker fetches first wins.
- **No fairness.** A hot publisher against the same key can starve a long-blocked job indefinitely. If you need ordering, build a job-per-stream chain instead of relying on Wait.

### New: `IConcurrencyLimitManager` — runtime-editable limits

Attribute and fluent limits are compile-time defaults. `IConcurrencyLimitManager.AddOrUpdateLimit("key", N)` lets you raise or lower a key's effective limit at runtime; the override is persisted on a new `ConcurrencyLimit` entity and takes precedence. Resolver precedence: `admin row > attribute > 1`.

Surfaced as a new dashboard page at `/warp/concurrency` (visible only when `AddConcurrency` is registered — the SPA probes `/api/concurrency` and hides the nav entry on 404). Five REST endpoints: list / get / set / clear / clear-all.

### New: `stats:requeued` counter + `/counters` dashboard page

Both the new Mutex Wait outcome and the existing Retry behavior produce requeue events. A new `stats:requeued` (and `stats:requeued:{hour}` for the rolling 7-day window) counter row is emitted by `WarpWorkerService` / `WarpDispatcherWorker` for any `Enqueued`/`Scheduled` outcome — so retry rate now has visibility for free.

These — and any addon-defined counter keys — surface on a new **Counters** dashboard page at `/warp/counters`. The page has two parts:

- **Hourly history chart** — every counter whose key suffix parses as `yyyy-MM-dd-HH` becomes a series. 24h / 7d toggle, Chart.js legend toggle, fixed colors for built-ins (`succeeded` green / `failed` red / `deleted` gray / `requeued` amber), deterministic hash-derived colors for addon series.
- **Rolled-up table** — non-hourly keys sorted lexicographically. Hourly variants are filtered out so the table stays readable.

`ExpirationCleanup` now prunes any `Statistic` row whose key suffix parses as `yyyy-MM-dd-HH` and is older than 7 days — generalized from the prior per-prefix logic (which also had a latent bug where `stats:succeeded:*` rows were never actually cleaned because the `CompareTo` bound was anchored to `stats:failed:`). Addon-defined hourly metrics get the same retention treatment for free.

### Breaking: consumer span renamed `Warp.Execute` → `process <queue>`

The job-execution span emitted on `WarpTelemetry.ActivitySource = "Warp"` is now named `process <queue>` per OTel messaging-spans convention (e.g. `process default`, `process critical`). The legacy `Warp.Execute` name no longer appears.

**If you matched on the literal `Warp.Execute` span name** (in exporter rules, alerting queries, dashboard filters), update those matchers. Prefer the OTel-native filter `messaging.operation.name = process` — that's stable across renames and matches every queue.

### Behavioural change: `Activity.Current` inside handlers without a listener

Previously, the worker's consumer activity was always allocated and `Activity.Current` was non-null inside every handler — even in deployments that never wired up `tracerBuilder.AddSource("Warp")`. This was a per-job allocation tax with no observable benefit for non-OTel users.

Now, when no `ActivityListener` is attached for the Warp source, the worker skips the allocation and `Activity.Current` is `null` inside the handler. Handlers that read `Activity.Current` to extract trace context (e.g. to forward to a downstream HTTP call) must either attach a listener (`tracerBuilder.AddSource("Warp")`), tolerate null, or use `JobExecutionContext.Current.TraceId` — which carries the job's trace id regardless of listener state.

### Adds: producer + receive + mediator + server-task spans

`Publisher.Enqueue` / `Publish` / `Schedule` and `BatchPublisher.StartNew` now emit a Producer-kind `send <queue>` activity per publish. Critically, `Job.ParentSpanId` still references the *caller's* span (the HTTP request, parent handler) — the producer span is a sibling event marker on the caller's trace, not the consumer's parent. Tests pin this invariant.

The worker emits a Client-kind `receive <queue>` span around post-fetch / pre-handler bookkeeping (mark ownership, log "Processing", commit). Receive precedes the consumer span and is a sibling under the caller's trace.

`IMediator.Send(TRequest)` and `IMediator.CreateStream(TRequest)` emit Internal-kind `process <RequestType>` spans wrapping the full pipeline + handler (or pipeline + stream enumeration). The stream activity lives across the entire `await foreach` and closes via `try/finally` even on early break or exception.

`ConcurrencyPipelineBehavior` (the unified Mutex+Semaphore behavior) emits a child `warp.concurrency_acquire` Internal-kind span around the acquire attempt with `warp.concurrency.key`, `warp.concurrency.limit`, and `warp.concurrency.acquired` tags.

Each `ServerTaskLoop` iteration (Heartbeat, Orchestrator, MessageRouter, RecurringJobScheduler, …) emits a `warp.server_task <Name>` Internal-kind span tagged with `warp.task.lock_held` and `warp.task.message`.

### Adds: `messaging.operation.type` and `messaging.message.conversation_id` on consumer span

OTel messaging conventions split the operation verb into `messaging.operation.name` (free-form, was already set) and `messaging.operation.type` (low-cardinality, now set alongside). The consumer also gains `messaging.message.conversation_id = job.TraceId`, `error.type` on failure, `warp.job.attempt`, `warp.worker.id`, and `messaging.batch.message_count` for batch jobs.

### Adds: mediator metrics

Two new instruments on `WarpTelemetry.Meter = "Warp"`:

- `warp.mediator.duration` — Histogram, ms, tags `kind` (`request`/`stream`), `request_type`, `status` (`succeeded`/`failed`/`cancelled`).
- `warp.mediator.in_flight` — UpDownCounter, tags `kind`, `request_type`.

### Wiring (no change)

```csharp
services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("Warp"))
    .WithMetrics(m => m.AddMeter("Warp"));
```

Single source + single meter, both named `"Warp"`.

### Fix: production deadlock retry in `CompletionBatch.FlushRangeAsync`

Under dispatcher mode (`UseDispatcher = true`) the completion batch occasionally hit a transient deadlock (SQL Server 1205, Postgres `40P01` / `40001`) on the parallel statistics + job-row update, which previously fell straight through to the existing split-on-failure path (slow, and lost the natural batch). Now `FlushRangeAsync` retries on transient deadlock with 50/100/200 ms exponential backoff before splitting.

Detection lives on a new `IDatabaseExceptionClassifier.IsTransientDeadlock` with provider-specific implementations in `Warp.Provider.PostgreSql` and `Warp.Provider.SqlServer` — exposed as a stable extension point.

### Fix: "Processing" log row no longer orphaned under dispatcher shutdown

`WarpDispatcher.FetchAndDistribute` wrote the `Processing` `JobLog` row *before* delivering the job to a worker's channel. If the channel write got cancelled during host shutdown, the row was orphaned (no `Completed` / `Failed` / `Cancelled` follow-up) and the dashboard showed a job stuck mid-flight that never actually ran. Moved to `WarpDispatcherWorker.MarkWorkerOwnership`, which runs after the worker actually starts processing the job. The log row now also carries the actual `WorkerId`, matching single-worker mode.

## 0.12.0

*2026-05-07*

### New: Moberg.Warp.Http

Optional package that exposes Warp `IRequest<TResponse>` and `IStreamRequest<TResponse>` handlers as ASP.NET Minimal API endpoints. Annotate the **handler class**, call `services.AddWarpHttp()` + `app.MapWarpHttp()`, and the endpoint is live. Source-generated dispatch (no per-request reflection); independent of `Moberg.Warp.UI`.

#### Quick start

Install `Moberg.Warp.Http` next to `Moberg.Warp.Core`, then tag handlers:

```csharp
using Microsoft.AspNetCore.Mvc;          // [FromRoute], [FromQuery], [FromHeader], [FromBody]
using Warp.Core.Handlers;
using Warp.Http;

public sealed record GetOrder([FromRoute] Guid Id) : IRequest<OrderDto>;

[WarpHttpGet("/orders/{id}")]
public sealed class GetOrderHandler : IRequestHandler<GetOrder, OrderDto>
{
    public Task<OrderDto> HandleAsync(GetOrder request, CancellationToken ct) =>
        Task.FromResult(new OrderDto(request.Id, "pending"));
}
```

Wire it up in `Program.cs` — independent of `Warp.UI`, composes with anything you already have:

```csharp
builder.Services.AddWarpHttp();

var app = builder.Build();
app.MapWarpHttp();      // null-group handlers
app.Run();
```

`GET /orders/{id}` is now live. The handler runs through the same `IPipelineBehavior<TRequest, TResponse>` pipeline as `IMediator.Send` — anything you've registered for cross-cutting concerns (auth, logging, validation, telemetry) applies automatically.

#### Binding leans entirely on ASP.NET Minimal API

No custom parser. `IParsable<T>`, `TryParse`, query arrays, nullable types, route constraints, content negotiation — all of it works because the source generator emits a Minimal API delegate that hands `TRequest` to ASP.NET:

```csharp
public sealed record ListOrders(
    [FromQuery] int Page,
    [FromQuery] int PageSize,
    [FromQuery] string[] Tags,                 // ?Tags=a&Tags=b → string[]
    [FromHeader(Name = "X-Trace-Id")] string TraceId)
    : IRequest<ListOrdersResponse>;

[WarpHttpGet("/orders")]
public sealed class ListOrdersHandler : IRequestHandler<ListOrders, ListOrdersResponse> { ... }
```

```csharp
public sealed record GetOrderTyped([FromRoute] Guid Id) : IRequest<OrderDto>;

[WarpHttpGet("/orders/{id:guid}")]                 // route constraint rejects non-GUIDs at routing time
public sealed class GetOrderTypedHandler : IRequestHandler<GetOrderTyped, OrderDto> { ... }
```

For mixed route + body shapes, declare a class with `[FromRoute]` / `[FromBody]` properties:

```csharp
public sealed class SubmitOrder : IRequest<OrderDto>
{
    [FromRoute(Name = "tenantId")] public Guid TenantId { get; set; }
    [FromBody]                     public SubmitOrderBody Body { get; set; } = new(string.Empty);
}

[WarpHttpPost("/orders/{tenantId}/submit")]
public sealed class SubmitOrderHandler : IRequestHandler<SubmitOrder, OrderDto> { ... }
```

A whole-body POST DTO without per-property attributes also just works — ASP.NET binds `TRequest` from the JSON body directly.

#### Streaming becomes Server-Sent Events

`IStreamRequest<T>` handlers turn into `text/event-stream` endpoints. `HttpContext.RequestAborted` propagates to the handler's enumerator, so a client disconnect ends the loop:

```csharp
public sealed record OrderFeed([FromQuery] int Count) : IStreamRequest<OrderEvent>;

[WarpHttpGet("/orders/feed")]
public sealed class OrderFeedHandler : IStreamRequestHandler<OrderFeed, OrderEvent>
{
    public async IAsyncEnumerable<OrderEvent> HandleAsync(
        OrderFeed request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < request.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return await NextEventAsync(ct);
        }
    }
}
```

Each `yield return` becomes one `data: {...}\n\n` SSE frame.

#### "Submit a job via HTTP"

`IJob` and `IMessage` cannot be HTTP-exposed directly — the source generator rejects them at compile time with `WHTTP001`. The recommended pattern is a thin `IRequest<Guid>` wrapper whose handler calls `IPublisher.Enqueue`. Explicit, debuggable, no framework magic:

```csharp
public sealed record EnqueueReport(Guid TenantId) : IRequest<Guid>;

[WarpHttpPost("/reports/generate")]
public sealed class EnqueueReportHandler(IPublisher publisher)
    : IRequestHandler<EnqueueReport, Guid>
{
    public async Task<Guid> HandleAsync(EnqueueReport req, CancellationToken ct)
    {
        var jobId = await publisher.Enqueue(new GenerateReportJob(req.TenantId));
        await publisher.SaveChangesAsync(ct);
        return jobId;                              // returns 200 with the job id JSON
    }
}
```

#### Auth, status codes, groups

`[Authorize]` / `[AllowAnonymous]` on the handler class surface as endpoint metadata, so group-level `RequireAuthorization(...)` composes naturally:

```csharp
[Authorize(Policy = "OrdersWrite")]
[WarpHttpPost("/orders/cancel")]
public sealed class CancelOrderHandler : IRequestHandler<CancelOrder, Unit> { ... }
```

```csharp
app.MapGroup("/api/public")
   .RequireAuthorization("publicPolicy")
   .MapWarpHttp("public");                        // only handlers with Group = "public"
```

Customize the response shape (status code, `Location` header) by implementing `IHttpResponseShape` on the response type — keeps the handler signature clean and the framework coupling localized:

```csharp
public sealed record CreatedOrder(Guid Id) : IHttpResponseShape
{
    public void Apply(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status201Created;
        ctx.Response.Headers.Location = $"/orders/{Id}";
    }
}
```

Multi-attribute is supported for versioning aliases (each attribute needs `Name = "..."`):

```csharp
[WarpHttpPost("/v1/orders", Name = "CreateOrderV1")]
[WarpHttpPost("/v2/orders", Name = "CreateOrderV2")]
public sealed class CreateOrderHandler : IRequestHandler<CreateOrder, OrderDto> { ... }
```

#### Compile-time diagnostics

| Code      | Trigger                                                            |
|-----------|---------------------------------------------------------------------|
| `WHTTP001` | Handler is invalid (request type implements `IJob` / `IMessage`, or handler doesn't implement a recognized request/stream interface). |
| `WHTTP002` | Multi-attribute on a single handler without `Name = "..."` on every attribute. |

#### Response semantics

| Handler kind            | Status | Body                                       |
|-------------------------|--------|--------------------------------------------|
| `IRequest<TResponse>`   | 200    | JSON of `TResponse`                        |
| `IRequest<Unit>`        | 204    | empty                                      |
| `IStreamRequest<T>`     | 200    | `text/event-stream` (one `data:` per item) |

Full docs at [features/http](features/http). Shipped in [#154](https://github.com/moberghr/warp/pull/154).

### Test Suite Improvements

- **HTTP request-isolation suite** — five concurrency tests bombard the in-memory test app with 200 parallel requests (50 for streaming) and assert per-request DI scope (N requests → N distinct `ScopeProbe` constructions), no `AsyncLocal<T>` bleed between handlers, no request-payload crossover, pipeline behaviors observe per-request inputs, and concurrent `IStreamRequest` endpoints emit only their own block of items.

## 0.11.0

*2026-05-06*

Stability release. No breaking API changes. Several latent product bugs surfaced as flakes during a test-infrastructure refactor and are fixed here; pause's "not instant" semantics are now spelled out in the public API surface.

### Improvements

- **Pause is documented as heartbeat-driven** — `IServerCommandService.PauseServer` / `PauseWorkerGroup` only stamp `PausedAt` on the DB row. Each server's worker pool keeps fetching until that server's next `Heartbeat` tick (cadence `HealthCheckInterval`, default 3s) refreshes its in-memory `PauseStateHolder`, and an in-flight worker iteration that already passed its pause check will still complete its current claim. Treat pause as "no new fetches after up to one heartbeat", not as a synchronous barrier — callers needing hard quiesce semantics combine pause with a wait of `HealthCheckInterval + PollingInterval`. The previous docs implicitly suggested instant propagation; the type docs now match the actual behavior.
- **`HealthCheckInterval` is now nullable** — `WarpWorkerConfiguration.HealthCheckInterval` is `TimeSpan?`. Set it to `null` to disable the auto `Heartbeat` loop entirely (the task stays DI-resolvable for manual invocation). Useful for tests that need to drive heartbeat ticks deterministically; existing values continue to work unchanged.
- **MIT LICENSE file** — repository root now ships a proper `LICENSE` file alongside the `MIT` `PackageLicenseExpression` already present in NuGet metadata, and the `README` has a license section pointing at it ([#149](https://github.com/moberghr/warp/pull/149)).

### Bug Fixes

- **Dispatcher shutdown is now exception-safe** — `WarpDispatcher.ExecuteAsync` wraps its loop in `try/finally` so the channel writer is always completed and the per-host registration disposed, even when an unexpected exception escapes the loop body. Without this, a non-`OperationCanceledException` slipping through the catch could leave dispatcher workers blocked on `WaitToReadAsync` until `IHostOptions.ShutdownTimeout` (default 30s) fired, masking the real failure. Closes the `DispatcherShutdownIntegrationTests` flake from [#151](https://github.com/moberghr/warp/pull/151).
- **`WarpServerRegistration.StopAsync` cleanup uses a fresh CTS** — the cancellation token passed to `StopAsync` is often already cancelled by the time host shutdown reaches the registration's stop method; reusing it left the cleanup queries (delete `Worker` / `WorkerGroup` / `Server` rows for this server id) in `(canceled)` state, leaving orphan rows for `ServerCleanup` to find on the next host's tick. Now uses a bounded fresh `CancellationTokenSource` (10s) so graceful cleanup completes regardless of upstream cancellation.
- **`ServerTaskLoop` survives transient SQL hiccups** — `EnsureRegisteredAsync` and the main iteration are now wrapped in `try/catch` with a short backoff. Previously a transient `SqlException` (e.g., a "session busy" / "severe error" under load) escaping these methods would crash the whole `ServerTaskHost` because `BackgroundServiceExceptionBehavior=StopHost` is the .NET default. Errors are logged and retried on the next iteration.
- **`MessageRouter` fires push notifications for routed children** — when a `Kind=Message` job fans out into N `Kind=Job` children, the router now captures the pending `JobEnqueued` notifications and fires them after `SaveChanges`. The dispatcher (when `UseDatabasePush()` is enabled) wakes immediately instead of waiting for the next poll. Idle-to-burst pickup latency for messages with many handlers drops from ~`PollingInterval` to &lt;50ms.
- **`DeleteJob` / `RequeueJob` no longer race the worker keep-alive** — `IWarpSqlQueries` previously had two lock-by-id variants: `LockJobByIdAsync` (with `READPAST` / `SKIP LOCKED`) for user commands and `LockJobByIdWaitAsync` (blocking) for orchestration. The `READPAST` variant raced the worker's brief keep-alive UPDATEs and would falsely return "job not found" while the row was being touched. The two methods are now unified onto the blocking `LockJobByIdWaitAsync`; the wait is bounded by the keep-alive's own commit, so it's microseconds in practice.
- **Per-host `DispatcherRegistry` replaces process-wide static** — `WarpDispatcher`'s notification-wakeup signal list was previously a `static List<>` on the class. With multiple `IHost`s in one process (typical in integration tests, possible in some embedded scenarios) the static cross-signaled dispatchers across host boundaries and held references to disposed dispatchers' semaphores. Now a DI singleton scoped to each host's container, registered automatically by `AddWarpWorker`.

### Test Suite Improvements

- **Per-class `IClassFixture` topology** — integration tests no longer share a single `WarpTestServer` per "shard" fixture; each test class gets its own database and each test boots its own server inside the test body. Eliminates whole categories of cross-test interference (leftover `Server` rows poisoning `ServerCleanup`, prior-test mid-flight jobs racing the next test's assertions, SQL Server connection-pool poisoning across server-replacement scenarios). The `[GenerateDatabaseTests(...)]` source generator now emits `[Xunit.IClassFixture<>]` per concrete subclass instead of `[Collection(...)]`.
- **Service Broker is opt-in** — SQL Server Service Broker is no longer always-on for every `_SqlServer` test. Tests that exercise DB push opt in via `[GenerateDatabaseTests(WithPush = true)]`, which routes them to the dedicated `SqlServerPushClassFixture`. Saves significant setup time for the polling-only suite.
- **Diagnostic dump on integration-test failure** — `FixtureDiagnostics.DumpAsync` runs on every failed integration test and prints stuck-job state, recent `ServerTask` / `ServerLog` rows, and a process-wide `ServerLifecycleTrace` of `IHost.StartAsync` / `StopAsync` events. The lifecycle trace is critical because `WarpServerRegistration.StopAsync` deletes its own `Server` row on graceful shutdown, so post-mortem queries against that row return nothing — the in-memory trace is the only source of truth for "did this server actually finish booting / shut down cleanly?" ([#147](https://github.com/moberghr/warp/pull/147), [#151](https://github.com/moberghr/warp/pull/151)).
- **`WarpTestServer.RunHeartbeatOnceAsync`** — test helper that resolves `Heartbeat<TestContext>` in a fresh scope and calls `ExecuteAsync` directly, sidestepping the `ServerTaskHost` auto-loop. Lets tests that disable `HealthCheckInterval` flip `PauseStateHolder` deterministically. Used by the rewritten `PauseServer_JobsStayEnqueued` / `PauseWorkerGroup_JobsStayEnqueued` tests.
- **Full suite stability** — 1,025 tests, ran 5 consecutive full-suite passes with zero flakes (1m 03s – 1m 11s) before tagging this release.

### Website & Docs

- **Enterprise landing page redesign** — new home page with a Moberg-aligned enterprise aesthetic, replacing the prior tagline-driven layout ([#148](https://github.com/moberghr/warp/pull/148), [#150](https://github.com/moberghr/warp/pull/150)).

---

## 0.10.0

*2026-04-27*

The library has been **renamed from Jobly to Warp**. NuGet package IDs change from `Moberg.Jobly.*` to `Moberg.Warp.*`, public types/namespaces from `Jobly.*` to `Warp.*`, default schema from `"jobly"` to `"warp"`, and the dashboard URL from `/jobly` to `/warp`. The old `Moberg.Jobly.*` packages will be deprecated on nuget.org with pointers to the new IDs.

### New Features

- **Auto handler registration** — Handlers and pipeline behaviors register themselves. The `Warp.SourceGenerator` now emits a `[ModuleInitializer]` per consumer assembly that pushes its handler / pipeline-behavior DI registrations into a process-level `WarpGeneratedHandlerRegistry` at assembly load. `AddWarp` replays the registry onto the user's `IServiceCollection` — so nothing else is needed.

  ```csharp
  // before
  services.AddHandlers(typeof(Program).Assembly);
  services.AddPipelineBehaviors(typeof(Program).Assembly);
  services.AddWarp<AppDbContext>(opt => opt.UsePostgreSql());

  // after
  services.AddWarp<AppDbContext>(opt => opt.UsePostgreSql());
  ```

  Works across solution boundaries — each assembly with handlers only needs the `Warp.SourceGenerator` analyzer reference (transitively present via `Moberg.Warp.Core`).

### Improvements

- **Generator covers all behavior kinds** — `IPipelineBehavior<,>` and `IStreamPipelineBehavior<,>` now flow through the source generator alongside `IPublishPipelineBehavior<>` (previously only publish behaviors did, the rest relied on the reflection scan). Open-generic implementations are emitted through the `services.AddTransient(typeof(IFace<>), typeof(Impl<>))` overload, matching the semantics the reflection path provided.
- **`IMessageHandler<T>` multi-handler fix** — The generator's per-message-type handler map was a `Dictionary` that silently overwrote earlier handlers when a message had multiple subscribers. Now it collects them all so pub/sub with N handlers registers N `AddTransient` entries and produces N child jobs — the behavior the reflection path already had, now available to the source-generated path.
- **Scoped behavior scan** — The generator's behavior scan is restricted to the *current* compilation (was walking referenced assemblies via `GetAllTypes`). Core's opt-in addon behaviors (`MutexPipelineBehavior<,>`, `RetryPipelineBehavior<,>`, `CircuitBreakerPipelineBehavior<,>`, `NoRestartPublishBehavior<>`) are still registered only by the explicit `AddMutex` / `AddRetry` / `AddCircuitBreaker` / `AddNoRestart` calls. Core's own compilation short-circuits generation entirely.

### Bug Fixes

- **SQL Server push setup stall is now cancellable** — `SqlServerNotificationTransport.PublishAsync` and `ListenAsync` now await the cached `_setup.Value` via `Task.WaitAsync(ct)` instead of a raw `await`. A stalled broker setup (e.g., schema lock contention on a busy SQL Server) no longer blocks every caller indefinitely — each caller's `CancellationToken` bails out of the wait without invalidating the cached setup task. The next caller with a live token re-awaits and proceeds. This closes the class of intermittent `"Test execution timed out after 30000 milliseconds"` failures in `SqlServerDatabasePushIntegrationTests` under heavy CI contention.
- **Race in `ServerTaskLoop.Signal` fixed** — `Signal()` had a check-then-act TOCTOU on its `SemaphoreSlim(0, 1)`: two threads could both pass `CurrentCount == 0` and both call `Release()`, second throwing `SemaphoreFullException`. Surfaced under dispatcher-mode contention (many workers calling `SignalJobFinalized` concurrently as batches flush) as `"Adding the specified count to the semaphore would cause it to exceed its maximum count"` and cascading downstream failures in the affected task loop. Fixed with a lock around the check-and-release — same pattern `WarpDispatcher.SignalAll` already used. Regression test runs 32 threads × 500 calls concurrently and asserts no exception.

### Test Suite Improvements

- **`TimedFact` default 30s → 10s** — Individual tests should finish in seconds; the 30s default was a band-aid for overly generous inner waits and could hide real hangs. Tests exercising deliberately slow behaviour (retry chains, multi-job integration workloads, two-server orchestration) opt in explicitly with `[TimedFact(N_000)]`. Twenty-three inner-wait timeouts (retry / cancellation / batch / continuation tests) were tightened from 15–30s to 5–10s to match actual runtime, with comfortable headroom for CI jitter.
- **Durability tests for "no job left unprocessed"** — Added three integration tests covering the core recovery guarantees that had no prior coverage:
  - `DispatcherShutdownIntegrationTests.GivenWorkInProgress_WhenServerReplaced_ThenAllJobsEventuallyComplete` — pod-rolling-restart scenario. Server A is disposed mid-flight, server B takes over the same queue; every enqueued job must reach `Completed` via whichever recovery path fires (`UnclaimUndelivered`, channel drain, or `StaleJobRecovery`).
  - `PushFailurePollingBackstopTests.GivenPushEnabledButTransportBroken_WhenJobEnqueued_ThenPollingStillPicksItUp` — proves that when the notification transport is completely broken (both `PublishAsync` and `ListenAsync` throw), polling still delivers the job within a small multiple of `PollingInterval`. Protects the "polling is the correctness backstop for push" invariant.
  - `ListenerReconnectDrainTests.GivenListenerAlwaysFails_WhenJobEnqueued_ThenReconnectDrainStillDelivers` — proves that `NotificationListenerTask.DrainSignals` fires on every reconnect iteration, waking the dispatcher even while the listener connection is permanently down. Jobs enqueued during the listener's offline window are not stranded.
- **`WorkerHostMode` lifecycle smoke tests deflaked** — two `*_CompletesLifecycleWithoutThrowing` tests were occasionally hitting the `TimedFact` budget on SQL Server CI under shared-container contention. Pre-cancel the token passed to `StartAsync` / `StopAsync` so each `BackgroundService.ExecuteAsync` short-circuits on its first `stoppingToken` check — DI wiring + constructors are still exercised, just without the polling loop. Local SQL Server: 360ms → 16ms.
- **SQL Server integration test deflakes** — two test-setup artifacts that surfaced as ~25–50% flake rates on shared SQL Server CI runners. (1) Per-test-server SqlConnection pool isolation: each `WarpTestServer` now gets a unique `Application Name` (which Microsoft.Data.SqlClient includes in the pool key) so a disposed server's cancel-poisoned connections never reach the replacement server's pool — this mirrors production pod-restart, where new process = new pool. Skipped on Npgsql to avoid blowing past `max_connections=100`. (2) The auto `CounterAggregator` 5s sweep was racing test assertions that read `Counter` rows directly; `CounterAggregationInterval` is now `null` in test defaults — every test that needs aggregation already triggers it explicitly via `TestTasks.CreateCounterAggregator`.
- **Full suite runtime** — now **~1m 30s** for 1,024 tests (was ~2m 40s for 947 in 0.8.0), down primarily from the inner-wait tightenings and the shorter `TimedFact` default.

### Migration

Breaking release because of both the rename and the removal of reflection-based registration helpers.

- **Switch to `Moberg.Warp.*` packages** — `Moberg.Jobly.Core` → `Moberg.Warp.Core`, `Moberg.Jobly.UI` → `Moberg.Warp.UI`, `Moberg.Jobly.Worker` → `Moberg.Warp.Worker`, `Moberg.Jobly.Provider.PostgreSql` → `Moberg.Warp.Provider.PostgreSql`, `Moberg.Jobly.Provider.SqlServer` → `Moberg.Warp.Provider.SqlServer`. The old IDs are deprecated on nuget.org but remain restorable.
- **Update namespaces, types, and method calls** — `Jobly.*` → `Warp.*` across all `using` directives and types. `AddJobly` → `AddWarp`, `IJoblyLockProvider` → `IWarpLockProvider`, `IJoblyCredentialValidator` → `IWarpCredentialValidator`, etc. A solution-wide find/replace of `Jobly` → `Warp` (case-sensitive) plus `jobly` → `warp` (lowercase, for env vars / strings) covers it.
- **Database schema** — default schema is now `"warp"` instead of `"jobly"`. Existing deployments must either rename the schema (`ALTER SCHEMA jobly RENAME TO warp;` on PostgreSQL; equivalent on SQL Server) or set `options.Schema = "jobly"` explicitly to keep the old name.
- **Dashboard URL** — `/jobly` → `/warp`. Update any reverse proxy / ingress rules and bookmarked links.
- **Drop the reflection calls** — if your `Program.cs` or test setup calls any of these, delete them:
  ```csharp
  services.AddHandlers(assembly);
  services.AddJobHandlers(assembly);
  services.AddMediatorHandlers(assembly);
  services.AddPipelineBehaviors(assembly);
  ```
  `AddWarp` replaces all of them for consumers that go through the normal DI path.
- **Test harnesses that build `ServiceCollection` manually** — if you construct an `IServiceCollection` directly (e.g. a unit test that doesn't go through `AddWarp`), call `services.AddWarpMediator()` from the generated `Warp.Core.Handlers.Generated` namespace. It stays public as an escape hatch and is idempotent with `AddWarp`.
- **Shared-handler assemblies need the analyzer** — if handlers live in a library that doesn't already reference `Moberg.Warp.Core`, add the source generator as an analyzer so its `[ModuleInitializer]` fires on load:
  ```xml
  <ProjectReference Include="path/to/Warp.SourceGenerator.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  ```
  In practice most consumers already pick this up transitively from `Moberg.Warp.Core`.

---

## 0.9.1

*2026-04-21*

### Bug Fixes

- **Docs site build** — `website/docs/features/db-push.md` was referenced from the 0.9.0 release notes but never added, breaking the Docusaurus production build with `onBrokenLinks: 'throw'`. The page is now present and mirrors the DB Push section of the README.

### Maintenance

- **Dashboard UI dependencies** — `npm audit fix` in `src/ui` patches `vite`, `axios`, `hono`, `@hono/node-server`, and `follow-redirects` (5 advisories, 1 high + 4 moderate). No API-surface changes; dashboard bundle hashes change because Vite re-bundles with updated transitive deps.
- **Docs site dependencies** — Docusaurus 3.9.2 → 3.10.0, added `@docusaurus/faster` (required by 3.10 with `future.v4`), and pinned `serialize-javascript ^7.0.5` via `overrides` to close [GHSA-qj8w-gfj5-8c6v](https://github.com/advisories/GHSA-qj8w-gfj5-8c6v) — the build-chain DoS that Docusaurus's bundler transitively pulled in. `npm audit` now reports 0 vulnerabilities on both frontends.

---

## 0.9.0

*2026-04-21*

### New Features

- **Database Push** — Opt-in push notifications replace polling wake-up for the dispatcher, `MessageRoutingTask`, and `OrchestrationTask`. Uses PostgreSQL `LISTEN`/`NOTIFY` or SQL Server Service Broker natively. Enable via `opt.UseDatabasePush()` inside the `AddWarp` / `AddWarpWorker` lambda. Dispatcher pickup drops from ~500ms to &lt;50ms; burst-and-idle workloads roughly halve wall-clock time. Idle deployments see ~16% fewer SELECTs (empty `FOR UPDATE SKIP LOCKED` fetches during idle go away). Worker-fetch push only wires when `UseDispatcher = true` — individual-worker mode keeps polling to avoid thundering-herd. Zero overhead if you don't opt in. See [DB Push](/docs/features/db-push).
- **`State.Scheduled`** — Future-dated jobs (`Schedule(job, at)`) now land in `State.Scheduled` instead of `State.Enqueued` with a future `ScheduleTime`. `ScheduledJobActivationTask` flips them to `Enqueued` when due and fires a `JobEnqueued` push. Cleans up the worker fetch predicate to a pure `CurrentState = Enqueued` check. Pre-upgrade rows still execute correctly thanks to a defensive `ScheduleTime <= now` filter.
- **Per-database Provider Packages** — Provider-specific code moves out of `Warp.Core` / `Warp.Worker` into two new NuGet packages: `Moberg.Warp.Provider.PostgreSql` and `Moberg.Warp.Provider.SqlServer`. Install the one that matches your database and opt in via `opt.UsePostgreSql()` / `opt.UseSqlServer()` inside the registration lambda. Core now stays fully provider-agnostic — no `Npgsql` or `Microsoft.Data.SqlClient` references.
- **Builder-based DI API** — `AddWarp<TContext>(opt => ...)` / `AddWarpWorker<TContext>(opt => ...)` take a single lambda over `IWarpBuilder<TContext>`. Config fields live on the builder directly (inherits `WarpConfiguration`); addons chain as extension methods (`opt.AddRetry()`, `opt.AddMutex()`, `opt.AddCircuitBreaker()`, `opt.AddNoRestart()`, `opt.UseDatabasePush()`).

### Improvements

- **Server-task architecture refactor (`IServerTask`)** — Every background task (`Heartbeat`, `ServerCleanup`, `StaleJobRecovery`, `CounterAggregator`, `ExpirationCleanup`, `RecurringJobScheduler`, `ScheduledJobActivation`, `Orchestrator`, `MessageRouter`) is now a plain DI-registered `IServerTask` service driven by a single `ServerTaskHost<TContext>`. Previously each task was a `BackgroundService` subclass of `ServerTaskBase` — the split separates domain logic from hosting mechanics. Task inner methods that were `public static` for test access are now `internal` instance methods, closing a class of lock-bypass bugs where tests and operators could accidentally invoke work outside the distributed lock.
- **Disable cleanup tasks at the config level** — `CounterAggregationInterval`, `ServerCleanupInterval`, `StaleJobRecoveryInterval`, `ExpirationCleanupInterval`, and `RecurringJobSchedulerInterval` are now `TimeSpan?`. Set any to `null` and the host won't auto-run that task's loop. Useful for multi-tenant setups where you want only one server running cleanup.
- **Signal plumbing consolidated** — `Orchestrator` and `MessageRouter` wake on push events via a single `ServerTaskSignals<TContext>` singleton with named `SignalJobFinalized` / `SignalMessageEnqueued` methods. Tasks declare their subscriptions via `IServerTask.Signals`. Replaces the old static `_instances` lists. No user-visible behaviour change.
- **Heartbeat no longer writes a `ServerLog` row per run** — under the old `ServerTaskBase` default, every server wrote a heartbeat success row every 3s. Now explicit per task via `LogOnSuccess`; heartbeat opts out. Failed heartbeats still log.
- **Faster CI builds** — Test workflow builds only `tests/Warp.Tests/Warp.Tests.csproj` instead of the full solution. Benchmarks, mutation tests, and demo apps no longer built in the test job; they come in transitively only where tests reference them.
- **Atomic-claim fetch** — Worker and dispatcher fetch are now `UPDATE ... RETURNING` (PG) / `UPDATE ... OUTPUT INSERTED.*` (SQL Server) via new `IWarpSqlQueries<TContext>` implementations in the provider packages. Closes the SELECT→UPDATE race window that produced rare double-claims under concurrent-worker load. The old regex-based `RowLockInterceptor` is retired.
- **Shutdown safety** — Three races that could leave jobs as `State=Processing` orphans on shutdown are fixed: `WarpDispatcher` un-claims rows it fetched but didn't deliver; `WarpDispatcherWorker` drains its channel fully before exiting; post-handler bookkeeping uses `CancellationToken.None` so a job whose handler already ran can't be abandoned mid-finalize.
- **Retry / CircuitBreaker respect `State.Scheduled`** — Delayed retries and circuit-breaker reschedules land in `State.Scheduled` when the target time is in the future. New `JobOutcome.RescheduledState(scheduleTime, now)` helper is shared by both pipeline behaviors. No change for immediate retries.
- **Worker host split** — `WarpWorkerSetup` is replaced by three DI-registered `IHostedService`s: `WarpServerRegistration`, `WarpDispatcherHost`, `WarpSingleWorkerHost`. Each mode no-ops when the other is selected via `UseDispatcher`. State flows via a new `ServerRegistrationState` singleton instead of re-querying.
- **Source layout** — Libraries under `src/core/`, providers under `src/core/providers/`, tests in `src/tests/`, demo apps in `src/demo/`. `Directory.Build.props` scoped to match.

### Bug Fixes

- **Queue-name encoding collision (SQL Server)** — `JobHelper` now rejects queue names containing the unit-separator (``) that SQL Server's `STRING_SPLIT` uses internally for encoding. Previously a job published to a ``-containing queue could be delivered to the wrong worker group.

### Migration

Breaking release because of the provider package split and the DI lambda API.

- **Install a provider NuGet**: add `Moberg.Warp.Provider.PostgreSql` or `Moberg.Warp.Provider.SqlServer` alongside `Moberg.Warp.Core`. The provider package registers the row-lock / atomic-claim queries, the exception classifier, and the notification-transport factory — all of which used to live in Core.
- **Wrap registration in a lambda + call the provider**:
  ```csharp
  // before
  services.AddWarp<MyContext>();
  services.AddWarpDatabasePush<MyContext>();   // if using push

  // after
  services.AddWarp<MyContext>(opt =>
  {
      opt.UsePostgreSql();                      // or UseSqlServer()
      opt.UseDatabasePush();                    // if using push, now chains on the builder
  });
  ```
- **`AddWarpDatabasePush<TContext>()` removed** — call `opt.UseDatabasePush()` on the builder instead. Must be after `UsePostgreSql` / `UseSqlServer`.
- **`State.Scheduled` is new** — Future-dated jobs existing from a previous version still execute correctly (worker fetch has a defensive `ScheduleTime <= now` predicate) but won't show in the dashboard's Scheduled list until their time arrives.
- **Retry / CircuitBreaker reschedules land in `State.Scheduled` when delayed** — dashboard filters and any external tooling that queried `Enqueued + future ScheduleTime` should now also look at `Scheduled`.

---

## 0.8.0

*2026-04-19*

### New Features

- **Exponential Polling Backoff** — Workers and the batch-fetch dispatcher now back off geometrically when queues are idle, reducing database load during quiet periods. Configure via `MaxPollingInterval` (default `30s`, ceiling) and `PollingIntervalFactor` (default `2.0`, multiplier). `PollingInterval` becomes the floor. On any processed job, the delay resets to the floor instantly, so throughput under load is unchanged. Paused workers stay at the floor (no compounding while paused). Available on both top-level `WarpWorkerConfiguration` and per-group `WorkerGroupConfiguration`. Set `PollingIntervalFactor = 1.0` to disable backoff. See [Operations → Configuration → Exponential Polling Backoff](/docs/operations/configuration#exponential-polling-backoff).
- **Retry Jitter** — New `JitterFactor` option on `RetryOptions` applies multiplicative random jitter to each computed retry delay: `delay * (1 + JitterFactor * rand(-1, 1))`. Clamped to `[0, 1]`. Global only — no per-job override. Defaults to `0.0` (no jitter) so existing behavior is unchanged. Use to spread retry attempts and avoid thundering herds when many jobs fail at once (e.g. downstream outage). The actual jittered `ScheduleTime` is recorded in the `Requeued` JobLog entry so operators can diagnose from the dashboard.
- **NoRestart (stale-recovery opt-out)** — New addon `AddWarpNoRestart()` lets specific job types stay `Failed` on worker crash instead of being auto-requeued. Apply with `[NoRestart]` / `[Restart]` attributes on the job class (inherits through the class hierarchy), `.WithRestart(bool)` per-enqueue, or flip the global default with `RestartStaleJobsByDefault = false`. Override chain: per-publish > attribute > global. For non-idempotent work (payments, emails, webhooks). See [NoRestart](/docs/features/no-restart).
- **Circuit Breaker** — New addon `AddWarpCircuitBreaker<TContext>()` stops hammering a failing downstream when failures cross a threshold. Opens after `Threshold` consecutive failures, stays open for `Duration`, then transitions to a **half-open state with an atomic probe gate** — exactly one worker probes while others reschedule, preventing thundering herd on recovery. Customise per-handler via `[CircuitBreaker(Group = "...", Threshold = N)]`. Adds a `CircuitBreakerState` entity to your DbContext (migration required). See [Circuit Breaker](/docs/features/circuit-breaker).
- **Batched Completions (Dispatcher Mode)** — When `UseDispatcher = true`, each worker now buffers job completions in memory and commits them as a single multi-row transaction, collapsing N per-job commits into one. Tune via `CompletionBatchSize` (default `50`) and `CompletionFlushInterval` (default `100ms`); set `CompletionBatchSize = 1` to opt out. Poison-entry isolation: a single bad row in a batch of 50 is dropped via recursive split; the other 49 still commit. SIGTERM mid-flush is safe — `FlushAsync` commits to completion using `CancellationToken.None` internally. Trade-off is at-least-once semantics; pair with `[NoRestart]` for non-idempotent handlers. See [Batched Completions](/docs/features/batched-completions).
- **Configurable Log Flush Interval** — New `LogFlushInterval` on `WarpWorkerConfiguration` (default `1s`) controls how often the job monitor drains handler `ILogger` output into the JobLog table. Lower values surface dashboard logs faster at the cost of more DB writes.

### Improvements

- **Resilient worker/dispatcher exception handling** — `WarpWorker` now catches transient exceptions in its poll loop (matching the dispatcher), so a single DB hiccup or handler pipeline fault no longer silently terminates the BackgroundService. The exception path uses a fixed floor delay instead of compounding the polling backoff, so jobs resume within `PollingInterval` of recovery instead of sitting at `MaxPollingInterval` for 30s.
- **`[NoRestart]` and `[Restart]` attributes are inherited** — Declare the policy on a base class (e.g. `PaymentJobBase`) and every derived concrete job inherits it. A derived class with its own attribute overrides the base — closest direct declaration wins.
- **Circuit breaker exception handling narrowed** — The `DbUpdateException` catch in `CircuitBreakerStore.RecordFailureAsync` now only suppresses unique-constraint violations (Npgsql `23505`, SqlClient `2627`/`2601`). CHECK, FK, and column-length violations propagate instead of being silently swallowed.
- **Test suite 48% faster, flake-free** — Disabled idle-polling backoff inside the test server, tuned `StaleJobRecoveryInterval` for tests only, made `LogFlushInterval` configurable, and bumped the `TimedFact` default from 10s to 30s. Full suite (947 tests) runs in ~2m 39s (was ~5m 05s). Eliminated the latent `TimedFact`/`WaitForJobState` timeout-mismatch class of flakes.

### Bug Fixes

- **Dispatcher SIGTERM completion-loss** — Pre-fix, `CompletionBatch.FlushAsync` drained its buffer before observing cancellation, and a shutdown mid-flush silently dropped the drained batch. Now commits with `CancellationToken.None` internally.
- **Circuit breaker thundering herd on half-open** — Without the new HalfOpen CAS, every worker polling when `OpenUntil` lapsed fired a concurrent probe against the recovering downstream. Fix guarantees exactly one probe fires per recovery window.
- **Retry jitter `ScheduleTime` not logged** — The `Requeued` JobLog entry now includes `(next attempt scheduled: <ISO timestamp>)` so operators can see the actual delay jitter applied.
- **MultiServer cross-test flakiness** — A too-aggressive `StaleJobRecoveryInterval` in the test fixture raced worker keep-alive refreshes under two-server SQL Server load, producing sporadic `DbUpdateConcurrencyException`.

### Migration

- **Circuit Breaker migration** — If you register `AddWarpCircuitBreaker<TContext>()`, a new `CircuitBreakerState` entity is added to your DbContext. Run an EF Core migration to create the table (`warp.circuit_breaker_state` under default schema + snake_case naming).
- **`CompletionBatch.FlushAsync` parameter removal** — If you were calling `CompletionBatch.FlushAsync(CancellationToken)` directly (rare — it's internal to `Warp.Worker`), the parameter has been removed. The method now always commits to completion.
- **`[NoRestart]` / `[Restart]` now inherit** — Behaviour change: a base class decorated with `[NoRestart]` now affects every derived concrete job. If you relied on the pre-release `Inherited = false` behaviour, add `[Restart]` on the specific derived types you want to opt back in.
- **`StaleJobRecoveryTask.RequeueStaleJobs` removed** — Replaced by `RecoverStaleJobs` returning `StaleJobRecoveryResult` (includes `Requeued`, `Failed`, `Deleted` counts). No `[Obsolete]` bridge; callers must migrate to the new signature.

---

## 0.7.0

*2026-04-17*

### New Features

- **Stream Requests** — New `IStreamRequest<TResponse>` pattern for lazy, item-by-item streaming via `IAsyncEnumerable<TResponse>`. Extends `IRequest<IAsyncEnumerable<TResponse>>` to preserve the unified type hierarchy — `IPipelineBehavior` applies automatically at the request level. New `IStreamPipelineBehavior<TRequest, TResponse>` wraps the actual enumeration for per-item concerns (timing, transforms). Resolved via `IMediator.CreateStream()`. Source generator provides zero-allocation dispatch.
- **Addon Architecture** — New `Outcome` on `IJobContext` (formerly `FailureOutcome`) lets pipeline behaviors control what happens on both success and failure. The worker is a generic state machine that applies the pipeline's decision. Combined with typed metadata and publish pipeline behaviors, this enables building composable addons (retry, mutex, dead letter queue, circuit breaker) entirely on top of Warp's public API. See [Building Addons](https://github.com/moberghr/warp/blob/main/docs/guides/building-addons.md).
- **Retry Addon** — Retry logic extracted from the worker into an opt-in module at `Warp.Core.Retry`. Declare retry policy with `[Retry(3)]` on either the handler or the job class, override per-enqueue with `new JobParameters().WithRetry(maxRetries: 5)`, or set global defaults via `services.AddWarpRetry(o => { o.MaxRetries = 3; o.Delays = [15, 60, 300]; })`. Priority: per-enqueue metadata > handler attribute > job attribute > global options.
- **Mutex Addon** — Mutex extracted from the worker hot path into an opt-in module at `Warp.Core.Mutex`. Register via `services.AddWarpMutex()`. Set keys with `new JobParameters().WithMutex("payment:123")` or `[Mutex("payment-processing")]` on the job class. Uses the new `IWarpLockProvider` abstraction for distributed locking.
- **Typed Metadata** — Access job metadata through strongly-typed interfaces. Define an interface extending `IJobMetadata`, and read it in handlers via `ctx.GetMetadata<IMyMetadata>()` or configure it at publish time with `new JobParameters().Configure<IMyMetadata>(m => m.CustomerName = "John")`. The source generator produces dictionary-backed implementations. `MetadataSerializer` uses native JSON deserialization for round-trip fidelity (integers stay as `long`, arrays as `List<object>`).
- **Recurring Job Enable/Disable** — Disable a recurring job to temporarily stop it from creating new jobs. The scheduler still fires on schedule but records a "Skipped" entry in the execution history. Re-enabling resumes from the next natural cron occurrence with no catchup burst. API: `POST /api/recurring/{id}/enable|disable`. Dashboard shows Enabled/Disabled badges and Skipped entries in history.
- **Worker Scope Isolation** — Worker and handler now use separate DI scopes. The handler's DbContext lives in its own scope — on failure, the scope is disposed and tracked entities are discarded. No partial handler work leaks into the worker's save. On success, handler changes are committed first (outbox pattern), then Warp state.
- **Extensible Dashboard UI** — New `IWarpUIExtension` interface lets external NuGet packages extend the dashboard without forking. Extensions ship an ES-module as an embedded resource, served at `/warp/_ext/{name}/`. The SPA dynamically imports each module and calls `install(warp)`. Extensions target `data-warp-slot` elements with `mount` / `append` / `insertBefore` / `insertAfter` operations, or register whole new pages via `addPage()`. React, ReactDOM, Axios, and shadcn components are exposed on `window.Warp` so extensions don't bundle them. The built-in `RetryUIExtension` is the reference implementation — renders a retry progress card with attempts/max and next-delay info on the job detail page.

### Improvements

- **Handler Registration Split** — `AddHandlers(assembly)` replaces the old `AddJobHandlers`. New granular methods: `AddJobHandlers` (job + message handlers only), `AddMediatorHandlers` (request + stream handlers only). `AddHandlers` calls both.
- **Dispatcher Split** — `JobDispatcher` (worker job execution) and `MediatorDispatcher` (in-memory request/stream dispatch) are now separate classes with independent method caches.
- **xUnit v3 + Microsoft Testing Platform** — Test suite migrated to xUnit v3 with `UseMicrosoftTestingPlatformRunner`. New `[TimedFact]` / `[TimedTheory]` attributes enforce a 10-second default timeout per test, surfacing deadlocks and hangs globally.
- **Server Memory Benchmarks** — New benchmark project at `src/benchmarks/Warp.ServerBenchmarks/` with four benchmarks (`ScopeMemoryBenchmark`, `WorkerMemoryBenchmark`, `ServerMemoryBenchmark`, `MemoryStressTest`) and a custom `TotalAllocatedDiagnoser` that tracks allocations across all threads. Baseline: ~50 KB per job regardless of scale; 100K-job stress test shows 0.3 MB retained growth (no leak) at 420–496 jobs/sec steady throughput. Documented in [Operations → Benchmarks](/docs/operations/benchmarks).
- **Mutation Testing** — New `Warp.Tests.Mutation` project with an in-memory SQLite fixture runs 293 tests in ~10 seconds, enabling a full Core mutation run in ~30 minutes via `dotnet-stryker`. Baseline scores: **Core 99.60%** (743 killed / 3 survived), Worker 51.53%. Fixed a `RecurringJobPublisher` race condition surfaced during mutation analysis — `AddOrUpdateRecurringJob` now uses `IWarpLockProvider` to prevent duplicate inserts on concurrent calls.
- **.slnx Solution Format** — Migrated from `src/Warp.sln` to `src/Warp.slnx` (XML-based solution format).

### Bug Fixes

- **Recurring job race on concurrent update** — `RecurringJobPublisher.AddOrUpdateRecurringJob` could insert duplicate rows when called concurrently with the same name. Now uses `IWarpLockProvider` for exclusive access during the upsert.
- **Trace page group node highlighting** — Fixed group node highlighting and edge behavior in the trace visualization page.

### Migration

This is a large release with several breaking changes. Plan the upgrade accordingly.

- **Retry is opt-in** — Add `services.AddWarpRetry()` to enable retries. Without it, failed jobs go directly to `Failed`. Replace the removed `maxRetries` publisher overloads and `WarpConfiguration.RetryCount` with `[Retry(n)]` attributes, `new JobParameters().WithRetry(n)`, or the global options callback.
- **Mutex is opt-in** — Add `services.AddWarpMutex()` to enable mutex enforcement. The `JobParameters.Mutex` property is removed — use `.WithMutex("key")` or `[Mutex("key")]` instead. The `ConcurrencyKey` column on the `Job` entity is removed (keys now live in metadata).
- **Typed metadata API** — `IJobContext<T>` / `JobContext<T>` are removed. Read typed metadata via `ctx.GetMetadata<IMyMetadata>()` and configure at publish via `new JobParameters().Configure<IMyMetadata>(m => ...)`.
- **Reduced public surface** — `JobHelper`, `JobDispatcher`, `MetadataSerializer`, and EF interceptors are now `internal`. The Retry and Mutex addons demonstrate that everything needed to build addons is available through the public API — no `InternalsVisibleTo` needed.
- **Database migration required** — New `DisabledAt` column on `RecurringJob`, `Skipped` column on `RecurringJobLog`, `ConcurrencyKey` dropped from `Job`. Run an EF Core migration after upgrading.
- **Solution file renamed** — Update build scripts and IDE shortcuts from `Warp.sln` to `Warp.slnx`.

### Stats

- ~770 tests (PostgreSQL + SQL Server) + 293 SQLite mutation tests
- Core mutation score: 99.60% (746 mutants, 743 killed)

---

## 0.6.1

*2026-04-13*

### Bug Fixes

- **Scoped service resolution in lock provider** — `IDistributedLockProvider` singleton factory resolved `DbContextOptions<TContext>` from the root provider, but `AddDbContext` registers it as scoped. This threw `InvalidOperationException` when scope validation is enabled (e.g. `WebApplication.CreateBuilder()` in Development). Fixed by creating a scope inside the factory.

### Code Quality

- **Fix all 401 build warnings** — Zero warnings across the entire solution. Fixes include: regex DoS hardening (NonBacktracking), collection expression simplification, constructor formatting, CancellationToken propagation, string.Equals usage, and async call consistency. All rule suppressions via `.editorconfig` — no `#pragma` or `[SuppressMessage]` attributes.

---

## 0.6.0

*2026-04-12*

### New Features

- **OpenTelemetry Distributed Tracing** — Every job execution creates a `System.Diagnostics.Activity` with W3C-format TraceId, SpanId, and ParentSpanId. Trace context is automatically propagated through job chains: when a handler enqueues a child job, the child's `ParentSpanId` links back to the handler's span. Message routing and batch creation also propagate span context. New `ParentSpanId` column on the Job entity stores the spawner's span ID.
- **Span Attributes** — Job execution spans include OTel semantic convention tags (`messaging.system`, `messaging.destination.name`, `messaging.operation.name`, `messaging.message.id`) and Warp-specific tags (`warp.job.type`, `warp.job.kind`, `warp.job.status`, `warp.job.duration_ms`, `warp.job.retry_count`). Failed spans are marked with `ActivityStatusCode.Error`.
- **Span Events** — Key lifecycle moments recorded as events on the span: `warp.job.completed`, `warp.job.failed` (with exception info), `warp.job.retried` (with retry/max counts), `warp.job.cancelled`.
- **OTel Metrics** — Four `System.Diagnostics.Metrics` instruments via a `Meter` named `"Warp"`: `warp.job.duration` (histogram, ms), `warp.job.active` (up-down counter), `warp.job.completed` (counter with status tag), `warp.job.enqueued` (counter with kind tag). All tagged by queue and type for filtering.
- **Automatic Log Correlation** — `AddWarpWorker` configures `ActivityTrackingOptions` so TraceId, SpanId, and ParentId appear in log output by default. No additional configuration needed.

### Integration

All features are on by default with zero configuration. To export to OTel backends:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("Warp"))
    .WithMetrics(m => m.AddMeter("Warp"));
```

### Migration

This release adds a new nullable `ParentSpanId` column to the Job table. Run an EF Core migration after upgrading.

### Bug Fixes

- **Distributed lock credential stripping** — Connection string resolution now reads from `DbContextOptions` RelationalOptionsExtension instead of `Database.GetConnectionString()`, which strips passwords via Npgsql `PersistSecurityInfo=false`. Also handles `NpgsqlDataSource` configurations.
- **Server status indicators** — Dashboard servers page now shows heartbeat-based status dots: green (active), red (stale >30s), amber (paused). "Inactive" badge shown when heartbeat is stale.

### Stats

- 658 tests (310 PostgreSQL + 310 SQL Server + 38 unit)

---

## 0.5.0

*2026-04-09*

### New Features

- **Job Metadata** — Attach key-value metadata to jobs at publish time via `JobParameters.Metadata`. Metadata is inherited by child jobs, accessible in handlers via `IJobContext`, and visible in the dashboard. New `IPublishPipelineBehavior<T>` interface for cross-cutting metadata (e.g., adding tenant ID to every job automatically).
- **Pause / Resume** — Pause and resume job processing at the server or worker group level via dashboard or API. Paused workers stop picking up new jobs; in-progress jobs continue to completion.
- **Real-time Handler Logs** — Handler `ILogger` output is now flushed to the database every ~1 second during execution, instead of only after the handler completes. Logs are visible in the dashboard while the job is still processing.
- **Multi-server Integration Tests** — 16 new tests (8 per database) verify distributed coordination: row locks, advisory locks, orchestration, message routing, and mutex enforcement across two independent servers sharing one database.
- **Deterministic Query Ordering** — Job and message fetch queries now use explicit ordering by queue and schedule time, ensuring predictable behavior in multi-server deployments.
- **Naming Convention Support** — Entity configurations respect EF Core naming conventions (e.g., `UseSnakeCaseNamingConvention()`). All Warp tables default to the `warp` schema, configurable via `WarpConfiguration.Schema`.
- **Configurable Handler Logging** — `EnableHandlerLogging` option (default true) to suppress handler `ILogger` output from the JobLog table when not needed. Lifecycle events are always recorded.
- **AI-friendly Documentation** — Added `llms.txt` and `llms-full.txt` for LLM/agent consumption, following the llms.txt convention.

### Improvements

- Sidebar reorganized into logical groups: Patterns, Features, Operations, Dashboard
- Dashboard shows metadata alongside job payload as formatted JSON
- NuGet badges added to README
- Deterministic query ordering for predictable multi-server behavior

### Stats

- 632 tests (316 PostgreSQL + 316 SQL Server)

---

## 0.4.0

*2026-04-08*

### New Features

- **Source Generator** — Zero-allocation mediator and worker dispatch via compile-time source generation. Replaces runtime reflection in `JobDispatcher` for handler discovery and execution.

### Links

- [GitHub Release](https://github.com/moberghr/warp/releases/tag/0.4.0)

---

## 0.3.0

*2026-04-07*

### New Features

- Initial public release with core job processing, message queue, in-memory mediator, dashboard, recurring jobs, batches, cancellation, mutex, crash recovery, and tracing.

### Links

- [GitHub Release](https://github.com/moberghr/warp/releases/tag/0.3.0)
