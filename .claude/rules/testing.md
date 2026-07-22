# Testing Standards

> ~1,200 tests in `src/tests/Warp.Tests/`, xUnit v3 (`xunit.v3.mtp-v2`) + Shouldly + Moq + Respawn + Testcontainers (Postgres + MSSQL). Full suite ~1m 30s; per-category breakdown ~3s (NoDb) / ~1m 10s (PG) / ~1m 20s (SQL Server).

## Structure

- **§4.1** Tests are organised by **feature folder** (`Admin/`, `Core/`, `Features/Retry/`, `Worker/`, `Notifications/`, etc.), not by unit-vs-integration split.
- **§4.2** The `[GenerateDatabaseTests(FixtureKind.X)]` source generator (`src/tests/Warp.Tests.SourceGenerator/`) emits `_PostgreSql` and `_SqlServer` concrete subclasses from a single abstract base. Hand-write only the abstract base; the generator handles fixtures, collections, and the `Category` trait.
- **§4.3** Test categories (xUnit traits):
  - `NoDb` — ~150 tests, ~3s. No container, no DB, no fixture. Examples: `PollingBackoffTests`, `MetadataSerializerTests`, `DashboardAuthTests` (uses `Microsoft.AspNetCore.TestHost`).
  - `PostgreSql` / `SqlServer` — ~530 each (DB-paired tests generated from a single base). Use `FixtureKind.Default` (unit-style against real DB) or `FixtureKind.Integration` (`WarpTestServer` boots full worker + background tasks). Variants: `FixtureKind.BatchedCompletion`, `FixtureKind.MultiServer`.

## Timing & Determinism

- **§4.4** Every test-affecting attribute defaults to **10s** (`[TimedFact]`, `[TimedTheory]`). A short default surfaces deadlocks and hangs immediately. Tests exercising genuinely slow behaviour (retry chains, end-to-end workloads) opt in explicitly with `[TimedFact(N_000)]`. See `src/tests/Warp.Tests/TestData/TimedFactAttribute.cs`. **NEVER raise the budget to fix a flake** — root-cause via the diagnostics infra.
- **§4.5** No `Task.Delay` in tests **except** for handlers meant to be cancelled (`CancellableCommand`).
- **§4.6** Tests needing deterministic timing configure `HealthCheckInterval = null` and call `WarpTestServer.RunHeartbeatOnceAsync` to drive the pause-state holder flip explicitly.
- **§4.7** No spray-N concurrency tests. Use `BarrierSignal` to pin handlers inside the critical section and assert with `N=2`, not by spraying 50 jobs and hoping.
- **§4.7.1** **Heavy integration classes serialize; light tests parallelize.** DB tests run `maxParallelThreads: 4` (matches a CI runner's vCPUs — `xunit.runner.json`). Classes that boot one or more full `WarpTestServer` hosts (each = 5 workers @100ms poll + ~10 server-task timers) opt into a shared serialized xUnit collection via `[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]` so at most one heavy server hammers the shared container at a time — light classes still parallelize freely. This is the load-side cure for thread-pool/DB-pool **starvation** that surfaces as an *unrelated* timing flake (a starved process freezes a victim test's server mid-run — its own `ScheduledJobActivation` stops firing — and its async continuations can't get a thread). When a previously-stable timing test starts flaking right after a new heavy-host test lands, suspect neighbor contention and add the neighbor to the serialized collection **before** touching the victim's budget (never bump `[TimedFact]` to mask — §0.3/§4.4). Also assert the result of any `SemaphoreSlim.WaitAsync(timeout)` — it returns `false` on timeout, and silently proceeding masks the real failure behind a generic hang.

## Test Construction

- **§4.8** Each test calls exactly ONE public method on ONE class. State set up via direct DB inserts. Fresh `_fixture.CreateContext()` for arrange / act / assert — no shared change tracking.
- **§4.9** Integration tests (`IntegrationTestBase`) publish jobs and wait for completion via `Server.WaitForCompletion()` / `Server.WaitForJobState()`. Cancel long-running jobs at the end so they don't block teardown (~600ms vs 30s).
- **§4.10** Test handlers are simple: empty body, throw, increment a counter. No unnecessary abstractions.
- **§4.11** Test naming: `MethodName_Scenario_ExpectedResult`.
- **§4.12** Use Shouldly: `job.CurrentState.ShouldBe(State.Completed)`, `result.ShouldNotBeNull()`, etc.
- **§4.13** Test code may use `DateTime.UtcNow` directly. Production code uses `TimeProvider` (§5.7).

## Writing a Unit Test (template)

```csharp
[GenerateDatabaseTests(FixtureKind.Default)]
public abstract class MyTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;
    protected MyTestsBase(IDatabaseFixture fixture) => _fixture = fixture;
    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task MethodName_Scenario_ExpectedResult()
    {
        var ctx = _fixture.CreateContext();
        // arrange: direct DB inserts
        await ctx.SaveChangesAsync();

        // act: ONE call on ONE service
        var svc = new JobCommandService<TestContext>(_fixture.CreateContext(), TimeProvider.System, Options.Create(new WarpConfiguration()));
        await svc.DeleteJob(jobId);

        // assert: fresh context
        var job = await _fixture.CreateContext().Set<Job>().FindAsync(jobId);
        job.CurrentState.ShouldBe(State.Deleted);
    }
}
```
