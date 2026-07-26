# Spec — Operational-event notifier seam (`IWarpNotifier`)

**Date:** 2026-07-25
**Slug:** `operational-notifier-seam`
**Status:** proposed (plan-only; not yet implemented)

## Summary

Warp detects operational events it already knows about and hands each — **post-commit**, as a **redaction-safe snapshot** — to host-implemented `IWarpNotifier` sinks. The host decides what to do (Teams, Slack, email, PagerDuty, log). **Warp ships no channel integrations** — this is a pure seam, matching the `IWebhookDeliveryExhaustedHandler` / `IWebhookSigner` / `IWarpCredentialValidator` precedent. Alerting is *Warp notifying the operator that something is wrong* — distinct from webhooks (*the host notifying external subscribers of domain events*), which need a subscriber URL and are the wrong abstraction for internal self-reporting.

## Scope classification

**Feature** — new external public contract (`IWarpNotifier`, `WarpOperationalEvent` hierarchy, `WarpEventType`, `opt.AddNotifier<T>()`), multi-file, 3 dispatch-site edits + new Core seam + tests + docs. `security_impact = none` (no new auth/financial/infra surface), but the event snapshot is **§1.2 PII-owned** — it carries identity/metadata only, never `Job.Message` / webhook / saga payload bodies.

## Decisions locked with the user

- **v1 events = the three that already have a discrete, post-commit detection site:** `WebhookDeliveryExhausted`, `SagaForceCompleted`, `InstanceDown`. Each is a single guarded dispatch call right after an existing commit — no new detection logic, no schema change.
- **`JobDeadLettered` is deferred** to a fast-follow slice. The finalization signal is payloadless, so at-least-once dead-letter notification needs a re-query + a claim mechanism (marker column or cursor) + a first-enable watermark for the pile of pre-existing `Failed` rows — a real design cost the other three don't have. The `WarpEventType.JobDeadLettered = 1` value is **reserved** in the enum now so the taxonomy is stable, but nothing emits it in v1.
- **`InstanceDown` is sourced from the existing `StaleSwept` detection** (`ExpirationCleanup`), not a new heartbeat-lapse site. `WarpEventType.InstanceHeartbeatLost` is *never emitted by production code today* (defined only for tests), so building on it would mean building the detection first — out of scope for v1.
- **`BacklogBreached = 5` is reserved** (depends on the not-yet-built queue-wait/backlog metrics) — enum value only, no v1 emission.

## Contract

```csharp
namespace Warp.Core.Notifiers;

public interface IWarpNotifier
{
    Task NotifyAsync(WarpOperationalEvent evt, CancellationToken ct);
}
```

- Resolved as `IEnumerable<IWarpNotifier>` (optional-marker pattern, §2.9). **Zero registered ⇒ feature inert** — no dispatch work happens.
- **Contract mirrors `IWebhookDeliveryExhaustedHandler` exactly** (§8.20): invoked **post-commit** (after the state transition is durable), **at-least-once**, and a **throwing notifier is caught, logged at Warning, and never propagated** — an alert sink must not take down the thing it observes. Guarded fan-out is centralised in `WarpNotifierDispatcher` (below) so all three sites share one copy of the try/catch (the `ExecuteWebhookDelivery.InvokeExhaustedHandlersAsync` pattern, `Warp.Core/Webhooks/ExecuteWebhookDelivery.cs:402-428`).
- **Captive-dependency footgun (§8.18):** notifiers are singletons — a notifier needing scoped deps injects `IServiceScopeFactory`, never `DbContext`. Documented; `ValidateScopes=true` catches it at startup.

## Event model (redaction-safe, §1.2)

Abstract base + typed subtypes so the host can `switch` on the concrete type; the `Type` enum backs quick filtering/logging.

```csharp
namespace Warp.Core.Enums;

public enum WarpEventType
{
    JobDeadLettered = 1,          // RESERVED — deferred slice, not emitted in v1
    WebhookDeliveryExhausted = 2, // v1
    SagaForceCompleted = 3,       // v1
    InstanceDown = 4,             // v1 (sourced from StaleSwept)
    BacklogBreached = 5,          // RESERVED — depends on queue-wait metrics
}

public enum WarpEventSeverity { Info = 1, Warning = 2, Error = 3 }
```

```csharp
namespace Warp.Core.Notifiers;

public abstract record WarpOperationalEvent
{
    public required WarpEventType Type { get; init; }
    public required WarpEventSeverity Severity { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public required string MachineName { get; init; }
    public string? Application { get; init; }   // WarpConfiguration.ApplicationName when set
    public required string Message { get; init; } // human-readable, non-PII summary line
}

// carries the existing WebhookDeliveryExhausted snapshot fields (identity/metadata only — no body/headers/secret)
public sealed record WebhookDeliveryExhaustedEvent : WarpOperationalEvent
{ public required Guid DeliveryId { get; init; } public required string EventType { get; init; }
  public required string EventId { get; init; } public required string Url { get; init; }
  public string? GroupName { get; init; } public string? Reference { get; init; } public required int AttemptCount { get; init; } }

// mirrors the existing ForceComplete audit LogInformation fields (SagaCommandService.cs:123-129)
public sealed record SagaForceCompletedEvent : WarpOperationalEvent
{ public required Guid SagaId { get; init; } public required string SagaType { get; init; }
  public required string CorrelationKey { get; init; } public required int LinkCount { get; init; } }

public sealed record InstanceDownEvent : WarpOperationalEvent
{ public required Guid InstanceId { get; init; } public required string ApplicationName { get; init; }
  public DateTime? LastSeenAt { get; init; } public required bool IsServer { get; init; } }
```

**Severity defaults:** `WebhookDeliveryExhausted` → Warning; `SagaForceCompleted` → Info (operator-initiated); `InstanceDown` → Warning.

**PII note (§1.2):** no event carries a payload body. `SagaForceCompletedEvent.CorrelationKey` is included because the existing `ForceComplete` audit already logs it at Information (§8.17 `SagaPiiCheck` blocks PII correlation keys at registration), so surfacing it here is consistent — documented, not silently new.

## Dispatch architecture (hybrid — confirmed by investigation)

All three v1 events are discrete, single-site, post-commit — so **direct guarded dispatch at each site** via the shared `WarpNotifierDispatcher`. (The signal-driven `JobDeadLettered` path, which *would* need a `BackgroundService` consuming `ServerTaskSignal.JobFinalized`, is the deferred slice.)

```csharp
namespace Warp.Core.Notifiers;

// Singleton, registered by AddWarp. Injects IEnumerable<IWarpNotifier>. One guarded loop, shared by all sites.
internal sealed class WarpNotifierDispatcher
{
    // await each notifier; catch + LogWarning + continue; never throws. No-op fast path when the set is empty.
    public async Task DispatchAsync(WarpOperationalEvent evt, CancellationToken ct) { ... }
}
```

### v1 dispatch sites

| Event | Site (file:line) | Insert |
|---|---|---|
| `WebhookDeliveryExhausted` | `Warp.Core/Webhooks/ExecuteWebhookDelivery.cs` — beside the existing `InvokeExhaustedHandlersAsync` call (post-commit, ~`:215`) | Build `WebhookDeliveryExhaustedEvent` from the same `WebhookDeliveryExhausted` snapshot already constructed; dispatch alongside the existing webhook-specific handler. The two coexist: the typed handler is webhook-specific, the notifier is the generic operational channel. |
| `SagaForceCompleted` | `Warp.Core/Services/SagaCommandService.cs:119` (after `SaveChanges`, beside the audit `LogInformation` at `:123`) | Dispatch `SagaForceCompletedEvent`. |
| `InstanceDown` | `Warp.Worker/Services/ExpirationCleanup.cs:606` (after the StaleSwept `SaveChangesAsync`) | Dispatch one `InstanceDownEvent` per swept `ApplicationInstance`. **[ASSUMED]** also source stale-*server* removals from `ServerCleanup` if it has an equivalent stale-server delete site (every server IS an instance, §8.23) — confirm the exact site during implementation; if absent, v1 covers non-server instances only and stale-server coverage joins the follow-up. Flagged for the approval gate. |

**Hot path sacred (§0.2/§6.1):** none of these sites is the worker fetch/execute path. The worker is untouched. Dispatch happens in the webhook executor job, the saga command service (cold admin path), and the `ExpirationCleanup` server task.

**§0.5:** dispatcher injects `IEnumerable<IWarpNotifier>` (specific dep); sites inject `WarpNotifierDispatcher`; no `IServiceProvider`, no `InternalsVisibleTo`.

## Registration

```csharp
public static IWarpBuilder AddNotifier<T>(this IWarpBuilder builder) where T : class, IWarpNotifier
    => // builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IWarpNotifier, T>()); return builder;
```

- Non-generic `IWarpBuilder` receiver (§2.13, the `AddBackgroundService<T>` precedent — the seam needs no `TContext`).
- `WarpNotifierDispatcher` is registered **once by `AddWarp`** (`Warp.Core/ServiceConfiguration.cs`, where `ServerTaskSignals<TContext>` already lives) as `TryAddSingleton`, so every dispatch site resolves it regardless of whether any notifier is registered — empty enumerable = inert. No dashboard/nav surface, so **no presence marker needed**.

## Where it lives

- New folder **`Warp.Core/Notifiers/`**: `IWarpNotifier`, `WarpOperationalEvent` + 3 subtypes, `WarpNotifierDispatcher`, `NotifierServiceConfiguration` (`AddNotifier<T>`). (`Warp.Core/Notifications/` is taken by the DB-push transport layer — reusing it would collide conceptually.)
- Enums `WarpEventType` + `WarpEventSeverity` in `Warp.Core/Data/Enums/` (namespace `Warp.Core.Enums`, per §8.13 convention).

## Test manifest

- **NoDb (`Warp.Tests/Notifiers/`):**
  - dispatcher fires every registered notifier exactly once for an event;
  - a throwing notifier is swallowed + logged at Warning, and the other notifiers still fire (order-independent);
  - zero notifiers registered ⇒ `DispatchAsync` is a no-op, never throws;
  - `AddNotifier<T>` registers into the `IEnumerable<IWarpNotifier>` (two `AddNotifier` calls ⇒ both resolve);
  - each event subtype carries no payload body (assert the redaction-safe field set).
- **DB, both providers (`[GenerateDatabaseTests]`):**
  - webhook exhaustion dispatches a `WebhookDeliveryExhaustedEvent` (extend the existing exhaustion test path);
  - saga `ForceComplete` dispatches a `SagaForceCompletedEvent`, and the notifier sees the saga row **already removed** (post-commit proof);
  - a stale `ApplicationInstance` swept by `ExpirationCleanup` dispatches an `InstanceDownEvent`.
- **§4 compliance:** bare `[TimedFact]`; NoDb dispatcher tests light; DB tests placed by feature folder.

## Assumptions & risks

- **[ASSUMED]** `ServerCleanup` has a stale-server removal site suitable for sourcing `InstanceDown` for worker servers; if not, v1 covers non-server instances only (flagged at the gate).
- **[VERIFIED]** finalization signal is payloadless (`ServerTaskSignals.cs`), which is why `JobDeadLettered` is deferred.
- **[VERIFIED]** `IWebhookDeliveryExhaustedHandler` post-commit/at-least-once/never-throw pattern exists and is the contract to copy (`ExecuteWebhookDelivery.cs:402-428`).
- Risk: a slow notifier (e.g. a synchronous HTTP POST to Teams) blocks the dispatch site (webhook executor / ExpirationCleanup tick). Mitigation: document that `NotifyAsync` should be fast / fire-and-forget its own I/O; the dispatcher honours the `CancellationToken`. Not a v1 blocker but noted.

## Out of scope (v1)

- `JobDeadLettered` emission (deferred slice: signal-consumer `BackgroundService` + claim mechanism).
- `BacklogBreached` (needs queue-wait metrics).
- Any Warp-shipped channel (Teams/Slack/email) — pure seam only.
- A proper `HeartbeatLost` lapse-detection site (earlier-than-reap warning).
- Dashboard surface for notifier history (this is an outbound seam, not a dashboard feature).
