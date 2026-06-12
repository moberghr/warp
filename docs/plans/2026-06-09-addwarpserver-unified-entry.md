# Plan — Unify under `AddWarpServer<TContext>()`

Spec: `docs/specs/2026-06-09-addwarpserver-unified-entry.md`

## Batch 1 — Rename sweep + obsolete aliases
Scripted global rename across `src/**/*.cs` (core, tests, benchmarks, demo): token `WarpWorkerConfiguration` → `WarpServerConfiguration`, `WarpWorkerBuilder` → `WarpServerBuilder`. Rename file `WarpWorkerBuilder.cs` → `WarpServerBuilder.cs`; drop `sealed` on the builder. Then add the two `[Obsolete]` subclass aliases (after the sweep, so they aren't rewritten). Build must be analyzer-clean — TreatWarningsAsErrors turns any missed old-name reference (now obsolete) into an error, which is the safety net.

## Batch 2 — AddWarpServer + worker toggle
`WarpServerConfiguration`: add `public bool RunWorker { get; set; } = true;` and `public void DisableWorker() { RunWorker = false; WorkerCount = 0; }`. In `ServiceConfiguration`: `AddWarpServer<TContext>(configure)` builds `WarpServerBuilder`, invokes lambda, `TryAdd`s `IOptions<WarpServerConfiguration>` + `IOptions<WarpConfiguration>`, calls private `AddWarpServerCore<TContext>(services, runWorker)`. `AddWarpServerCore`: always `AddServerHostCore<TContext>`; if `runWorker`, add `DispatcherRegistry` + `JobLoggerProvider` logging block + the 6 job `IServerTask`s + `WarpDispatcherHost`/`WarpSingleWorkerHost`. Obsolete `AddWarpWorker` shim constructs `WarpWorkerBuilder`, invokes its `configure`, then the same core path. Delete `WarpBackgroundServicesBuilder.cs` and the `AddWarpBackgroundServices` method. `WarpServerRegistration.StartAsync`: `if (!_configuration.RunWorker)` → empty worker-group set; keep the per-group `WorkerCount==0` skip.

Read `RunWorker` from the resolved options inside `AddWarpServerCore` — it's set on the builder before `TryAdd`, so read `builder.RunWorker` directly (don't re-resolve IOptions during registration).

## Batch 3 — Tests
`DeploymentShapeTests`: replace the three `ServiceOnlyShape_*` tests with: `ServerWithWorkerShape_*` (AddWarpServer default registers worker hosts + 6 job tasks + bg host), `ServiceOnlyShape_AddWarpServerDisableWorker_OmitsWorker` (no worker hosts, none of 6 job tasks, bg host + 3 server tasks present), and `ObsoleteAddWarpWorker_StillRegistersFullWorker` (`#pragma warning disable CS0618`). `ServiceOnlyHostTestsBase`: change the host builder to `AddWarpServer(opt => { ...; opt.DisableWorker(); ... })`; keep all five assertions. `WarpServerRegistrationTests`: keep zero/mixed-group tests (renamed type); add `StartAsync_RunWorkerFalse_WorkerCountNonZero_NoGroups`.

Checkpoints: NoDb filter, then the two service-only DB classes, then the full suite (the rename touches everything — full green is the gate).

## Batch 4 — Docs + rules
Rewrite the `background-services.md` "Service-only deployment" section around `AddWarpServer` + `DisableWorker()`; update the tier table (`AddWarp` / `AddWarpServer` / `AddWarpServer + DisableWorker`). Update `architecture.md` §2.5/§2.13 and `project-specific.md` §8.18 to the server framing and note `AddWarpWorker` is obsolete.

## Final
Behavioral diff; spec-drift vs sidecar; compliance → test/architecture review; confirm worker hot path untouched.
