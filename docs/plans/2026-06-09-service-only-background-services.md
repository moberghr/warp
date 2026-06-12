# Plan — Service-only deployment (`AddWarpBackgroundServices`)

Spec: `docs/specs/2026-06-09-service-only-background-services.md`

## Batch 1 — Production code

**Files:** `src/core/Warp.Worker/WarpBackgroundServicesBuilder.cs` (new), `src/core/Warp.Worker/ServiceConfiguration.cs`

1. Add `WarpBackgroundServicesBuilder<TContext> : WarpWorkerConfiguration, IWarpBuilder<TContext>` — copy of `WarpWorkerBuilder` shape (ctor stores `Services`; `IWarpBuilder.Configuration => this`). XML summary notes worker-only fields are ignored in service-only mode.
2. In `ServiceConfiguration`, extract `private static IServiceCollection AddServerHostCore<TContext>(this IServiceCollection services)` from `AddWarpWorkerInner` containing the **shared** registrations: `AddWarp<TContext>()`, `PauseStateHolder`, `ServerRegistrationState`, `ProcessCpuTracker`, `HeartbeatLeaseTracker`, the three scoped bg-service coordinators, `IServerTask` Heartbeat/ServerCleanup/ExpirationCleanup, hosted `WarpServerRegistration` → `ServerTaskHost` → `BackgroundServiceHost` (guarded), `NullBackgroundServiceStatusObserver` (TryAdd).
3. `AddWarpWorkerInner` becomes: `AddServerHostCore<TContext>()` + worker-only registrations (`DispatcherRegistry`, the `AddLogging`/`JobLoggerProvider` block, the six job `IServerTask`s, `WarpDispatcherHost`, `WarpSingleWorkerHost`).
4. Add public `AddWarpBackgroundServices<TContext>`: build `WarpBackgroundServicesBuilder`, invoke lambda, set `builder.WorkerCount = 0` and `builder.UseDispatcher = false`, `TryAddSingleton` both `IOptions<WarpWorkerConfiguration>` and `IOptions<WarpConfiguration>` from the builder, then `AddServerHostCore<TContext>()`. XML doc mirrors `AddWarp`/`AddWarpWorker` style and states the provider is still required.

**Checkpoint:** `dotnet build src/Warp.slnx` analyzer-clean.

**Boundary:** do not touch worker hosts, dispatcher, or any `IServerTask` execution body (§0.2/§6.1). Registration wiring only.

## Batch 2 — NoDb shape tests

**File:** `src/tests/Warp.Tests/Admin/DeploymentShapeTests.cs`

Add three `[Trait("Category","NoDb")]` `[TimedFact]` tests mirroring the existing `*Shape_*` pattern + `RegisterMinimalDependencies`:
- `ServiceOnlyShape_AddWarpBackgroundServices_RegistersHostAndBgServices` — resolve core API + `IBackgroundServiceStateService`/`LeaseCoordinator`/`LogStore`; assert `BackgroundServiceHost`/`ServerTaskHost`/`WarpServerRegistration` present in the `IHostedService` descriptors.
- `ServiceOnlyShape_OmitsJobWorkerHostsAndTasks` — assert `WarpDispatcherHost`/`WarpSingleWorkerHost` absent from hosted-service descriptors; resolve `IEnumerable<IServerTask>` and assert it contains the 3 shared tasks and none of the 6 job tasks.
- `ServiceOnlyShape_AddBackgroundServiceAndProvider_Compose` — call `opt.AddBackgroundService<CountingService>()` inside the lambda; assert the `WarpBackgroundService` alias resolves.

**Checkpoint:** `dotnet test ... -- --filter-trait "Category=NoDb"`.

## Batch 3 — Integration tests (both DBs)

**File:** `src/tests/Warp.Tests/BackgroundServices/ServiceOnlyHostTestsBase.cs` (new, `[GenerateDatabaseTests]`, extends `IntegrationTestBase`)

Build a minimal service-only `IHost` inline (DbContext via fixture connection string + provider, `AddWarpBackgroundServices` with provider + `AddBackgroundService<BarrierPinnedService>`, short `HealthCheckInterval`). Reuse `BackgroundServiceBarrierSignal`/`BarrierPinnedService`/`CountingService` test doubles.
- `ServiceOnly_PerServerService_ReachesUserCode` — barrier `Running` released within budget.
- `ServiceOnly_CreatesServerRow_NoWorkerRows` — `Server` row exists for the server id; `Worker` + `WorkerGroup` counts are 0.
- `ServiceOnly_GracefulShutdown_DeletesInstanceRow` — instance row gone after host stop.

Follow `PerServerLifecycleTestsBase` precedent (`[TimedFact(15_000)]`, release barrier before teardown). No `Task.Delay`, no spray-N.

**Checkpoint:** `dotnet test ... -- --filter-trait "Category=PostgreSql"` and `Category=SqlServer` for the new class.

## Batch 4 — Docs + rules note

**Files:** `website/docs/features/background-services.md`, `.claude/rules/architecture.md` (§2.13), `.claude/rules/project-specific.md` (§8.18)

Document the new deployment tier (`AddWarp` publish-only → `AddWarpBackgroundServices` services-only → `AddWarpWorker` full worker), the provider requirement, and that no job worker/job server tasks run. One sentence into §2.13 and §8.18.

**Checkpoint:** none (docs).

## Final

- Full suite: `dotnet test --project src/tests/Warp.Tests/Warp.Tests.csproj`.
- Behavioral diff; spec-drift check vs sidecar; review (compliance → test/architecture).
