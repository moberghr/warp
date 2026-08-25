---
sidebar_position: 11.5
---

# Dashboard Push (optional)

Replaces dashboard polling with a SignalR push. Job-finalised and message-enqueued events fan out to every connected dashboard within one coalesce window, carrying the current `DashboardStatistics` DTO as the payload. Connected clients no longer trigger N × `GET /api/status` refetches — a single server-side stats query feeds every client per event.

Opt-in; without `opt.AddDashboardPush()` the dashboard runs entirely on REST refetch and a 30 s safety-net poll.

## Setup

```csharp
builder.Services.AddWarp<AppDbContext>(opt =>
{
    opt.UsePostgreSql();         // or opt.UseSqlServer()

    // Multi-server fanout — needed when more than one server processes jobs.
    opt.UseDatabasePush();

    opt.AddDashboardPush(o =>
    {
        o.CoalesceWindow = TimeSpan.FromMilliseconds(100);  // default
    });
});
```

The hub is mounted at `${RoutePrefix}/api/hub` (default `/warp/api/hub`). The frontend reads `${RoutePrefix}/api/addons` once at boot and falls back to 30 s polling when `push: false` (i.e. `AddDashboardPush()` was not called). Same discovery endpoint reports concurrency / rate-limit / saga addon availability for nav visibility.

## What gets broadcast

| Event | Trigger | Payload |
|---|---|---|
| `JobFinalized` | A job reaches a terminal state (`Completed` / `Failed` / `Deleted` / `Cancelled`). | `DashboardStatistics` snapshot. |
| `MessageEnqueued` | A new `IMessage` row is written and the router signals downstream consumers. | `DashboardStatistics` snapshot. |

Both events arrive as SignalR invocations with the DTO as the first arg. Clients update their navbar counts and chart series directly from the payload; per-view data (filtered job lists, job detail, logs) stays on event-driven REST refetch.

## Coalescing

Each signal sets a latched flag and releases the broadcaster's wake semaphore. The loop wakes, waits `CoalesceWindow` (default 100 ms), then emits **at most one broadcast per event kind per window**. A 50-job batch finalising in a 20 ms burst collapses to one `JobFinalized` broadcast, not 50.

`CoalesceWindow = TimeSpan.Zero` disables coalescing — every signal becomes its own broadcast. Useful only for tests that need deterministic counts.

## Multi-server fanout requires DB push

The broadcaster subscribes to in-process `ServerTaskSignals<TContext>`. In a single-server deployment that's enough — every signal fires on the same process that processed the job. In a multi-server deployment, signals raised by server A's workers don't reach server B's broadcaster unless `UseDatabasePush()` is also on; without it, clients connected to server A only see events from server A's workers until the 30 s safety-net poll closes the gap.

The broadcaster is the **third** consumer of `ServerTaskSignals<TContext>`, after `Orchestrator` and `MessageRouter`.

## Auth piggybacks on the dashboard's endpoint conventions

The hub is one of the endpoints `MapWarpDashboard` returns, so whatever you applied — `RequireAuthorization`, `RequireWarpDashboardLogin`, `RequireLocalRequests` — covers the SignalR negotiate (`POST /warp/api/hub/negotiate`) and the WebSocket-upgrade request too. An auth-protected dashboard needs no extra wiring, and there is no parallel auth code path.

## Connection states

The navbar shows a fixed-width status pill that cycles through:

- **Live** — connected to the hub.
- **Connecting** — initial handshake or first reconnect attempt.
- **Reconnecting** — transient drop, exponential-backoff retries.
- **Polling** — hub unavailable (404 probe, repeated reconnect failures); 30 s safety-net poll is driving the UI.

## OpenTelemetry

Two instruments on `WarpTelemetry.Meter = "Warp"`:

- `warp.dashboard.connections.active` — UpDownCounter, current SignalR connection count.
- `warp.dashboard.events.broadcast` — Counter, broadcast emissions tagged by event kind.

The broadcaster also logs at `Warning` on broadcast failure and stats-fetch failure (the stats fetch is best-effort — on failure the event is still broadcast without a payload so clients fall through to their REST refetch path).

## Out of scope (v1)

- **Per-user / per-job groups** (`Clients.User` / `Clients.Group`) — hub is broadcast-only.
- **Client-initiated hub commands** — hub is one-way (server → client).
- **Redis backplane** — DB push is the cross-server fanout.
- **Per-view push payloads** — job lists, job detail, and logs stay on event-driven REST refetch.
- **Removing the 30 s safety-net poll** — kept as a backstop for missed events / dropped reconnects.
- **User-facing polling toggle** — handled by addon presence vs. absence, not a runtime switch.
