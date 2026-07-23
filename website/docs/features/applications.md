---
sidebar_position: 14
---

# Multi-Application Observability

In a shared-database Warp deployment, many independent applications point at **one schema** (one database). That is a supported, first-class shape: all registered servers still pick up all jobs — execution and routing are **unchanged**. But without this feature every process's only identity is `Server.ServerName` (its machine name), so there is no way to tell *which application* created a job, made an adapter call, served an endpoint request, or sent a webhook. Worse, non-server processes — a publisher-only API, a dashboard-only host, anything that calls `AddWarp` but not `AddWarpServer` — are completely invisible: no row, no heartbeat, no liveness.

Multi-application observability adds an opt-in **application** origin/identity dimension on top of the shared schema, so you can see every process live or dead in one overview, attribute activity to the application that produced it, filter every list by application, and get per-application adapter/endpoint metrics plus per-job-type and per-handler execution metrics — all without changing job execution, and with **zero impact** when the feature is off.

## This is not isolation

Multi-application observability is about **visibility within one shared schema** — many apps, one database, all servers running all jobs. It does **not** isolate apps from one another.

If you need true isolation between genuinely independent applications — separate job queues, no cross-pickup, independent cleanup and retention — give each app its **own schema** via `opt.Schema` (see [the data layer / schema configuration](../getting-started.md)). That is a different deployment shape and a different tool. This feature is the answer to *"we already share one schema on purpose; now tell us who's who."*

## Opt-in: one config line

The entire feature is gated on a single nullable config value. Set `ApplicationName` and everything below turns on; leave it `null` (the default) and behavior is **byte-for-byte** what it is today.

```csharp
builder.Services.AddWarp<AppDbContext>(opt =>
{
    opt.UsePostgreSql();

    opt.ApplicationName = "orders-api";          // the trigger — null ⇒ feature off
    opt.ApplicationVersion = "2.4.1";            // optional, self-reported
    opt.ApplicationEnvironment = "production";   // optional, self-reported
});
```

`ApplicationName` is **cluster-wide identity**, exactly like an adapter name: **the same name means the same application** — its instances group together, its stats merge, its metrics accumulate as one. Two genuinely different applications must get two different names; the same application scaled to five replicas uses one name across all five (each replica is a separate *instance* under that one application).

`ApplicationVersion` and `ApplicationEnvironment` are per-instance, self-reported strings stamped on this process's row — replicas may legitimately report different versions mid rolling-deploy. Both are ignored when `ApplicationName` is `null`.

## What you get

### Every process is visible

Warp uses a **sibling-table** model: *every server is an application instance; not every instance is a server.*

- A **server** process (`AddWarpServer`) keeps its existing `Server` row, which now also carries `Application`, `Version`, and `Environment`, stamped by the existing `Heartbeat` server task. No new loop.
- A **non-server** process (`AddWarp`-only publisher / API / dashboard) writes one `ApplicationInstance` row.

Each process writes exactly **one** physical row — no double-writes. To make non-server processes visible, `AddWarp` now starts a lightweight heartbeat `IHostedService` **when `ApplicationName` is set** — it registers the instance, heartbeats CPU/RAM on `ApplicationHeartbeatInterval`, and deregisters on graceful `StopAsync`. It uses no provider and takes no distributed lock (each instance owns its row by `Id`).

:::note A deliberate contract change
`AddWarp` has historically been passive — it registers services and never starts a background loop. When `ApplicationName` is set, it now runs this one lightweight heartbeat host. This is intentional and narrowly scoped: gated on `ApplicationName`, no provider or lock required, and it deregisters cleanly on shutdown. With `ApplicationName == null` (the default) nothing starts and `AddWarp` stays passive exactly as before.
:::

Stale instances (a process that crashed without deregistering) are swept by `ExpirationCleanup` once their last heartbeat is older than `ApplicationInstanceStaleGrace`.

### Provenance on everything produced

When `ApplicationName` is set, the producing app's name is stamped as a nullable `Application` column on:

- **`Job`** — the **publishing** application (stamped at publish, preserved on requeue).
- **`AdapterCallLog`** — the app that made the outbound call.
- **`EndpointCallLog`** — the app that served the inbound request.
- **`WebhookDelivery`** — the app that sent the webhook.

Every dashboard list (Jobs, Adapters, Endpoints) gains a **global application filter** driven by these columns. Rows produced before the feature was enabled — or by an old-version process — have `Application = null` and surface under an **"(unassigned)"** bucket.

### Per-application metrics

Adapters and endpoints accrue **per-application** metrics (count, duration, error rate) in addition to their existing app-agnostic totals. `Application` also becomes part of **endpoint identity**, so the same route served by two applications stays two distinct aggregates rather than collapsing into one.

### Per-job-type and per-handler execution metrics

Job execution metrics — count, average/percentile duration, and error rate — accrue **by job type** and **by handler**, tagged by the **executor** application (the app whose worker actually ran the job). These fold into the same durable `Statistic` aggregates the rest of Warp uses, so they **survive job-row cleanup**: after a completed job's row is deleted, its contribution to the type/handler counts, average latency, and error rate remains. The dashboard's Jobs-by-Type view exposes both a by-type and a by-handler slice.

### Lifecycle log

A unified `ApplicationInstanceLog` records lifecycle events for **all** instances (server and non-server): `Registered`, `HeartbeatLost`, `Recovered`, `Stopped`, `StaleSwept`. This is distinct from `ServerLog`, which stays server-task-execution history and is relabeled **"Server tasks"** in the UI.

### Tracing

Activities carry a `warp.application` OpenTelemetry attribute when `ApplicationName` is set (absent when `null`), so you can correlate a request across applications in your trace backend.

### The Applications dashboard page

The former **Servers** page is now **Applications** (the `/servers` route redirects to `/applications`). It groups all instances — servers ∪ non-servers — into one roster via a unified `InstanceView`, with an application detail page and per-instance detail. Because `IApplicationQueryService` is registered by `AddWarp` itself, the page and its API resolve even in a dashboard-only / publisher-only process that never runs a server.

## Provenance vs execution — a deliberate asymmetry

`Job.Application` is the **publishing** application: which app *created* the job. It is for **filtering and tracing only**.

There are deliberately **no per-creator job metrics**. In a shared schema any worker runs any job, so the app that created a job is not the app that executed it — a "jobs per creating app" latency or throughput number would be meaningless. Execution metrics are instead attributed to the **executor** application (see above). So: creator is provenance (filter/trace), executor is metrics.

## Metrics durability

Counts, average latency, and error rate for adapters, endpoints, and job execution all come from folded `Counter → Statistic` aggregates — **not** from the raw call/job rows. They stay correct after retention prunes those rows. Hourly history buckets auto-prune at `WarpConfiguration.HourlyStatisticsRetention` (default 7 days); non-bucketed lifetime totals persist indefinitely. Only the recent-rows lists and last-failure timestamps read raw rows and degrade gracefully once those age out.

## Configuration

All knobs live on `WarpConfiguration` (set inside the `AddWarp` / `AddWarpServer` lambda). Everything is ignored when `ApplicationName` is `null`.

| Setting | Type | Default | Purpose |
|---|---|---|---|
| `ApplicationName` | `string?` | `null` | The opt-in trigger and cluster-wide application identity. |
| `ApplicationVersion` | `string?` | `null` | Self-reported build/assembly version, stamped on this process's instance row. |
| `ApplicationEnvironment` | `string?` | `null` | Self-reported environment (`production`/`staging`/…), stamped on this process's row. |
| `ApplicationHeartbeatInterval` | `TimeSpan` | `15s` | How often the non-server heartbeat host refreshes CPU/RAM + `LastHeartbeatAt`. |
| `ApplicationInstanceStaleGrace` | `TimeSpan` | `2m` | Liveness window; instances not heard from within it are shown offline and swept. Kept comfortably above the heartbeat interval so a merely-slow instance isn't reaped. |
| `ApplicationInstanceLogRetention` | `TimeSpan` | `7d` | Age cap for `ApplicationInstanceLog` rows. |
| `ApplicationInstanceLogRetentionCount` | `int?` | `null` (off) | Count cap — keep at most N lifecycle-log rows, deleting the oldest. |

## Migration and upgrade

The change is **100% additive**: two new tables (`application_instance`, `application_instance_log`) and seven new nullable columns (`server` gains `application`/`version`/`environment`; `job`, `adapter_call_log`, `endpoint_call_log`, and `webhook_delivery` each gain `application`). There are no drops, renames, retypes, or new NOT-NULL-without-default columns.

Because `WarpModelCustomizer` contributes the two entities to your model unconditionally (they are [always in the schema](./adapters.md#always-in-the-schema), like the other addon entities), your **standard migration flow** picks the delta up:

```bash
dotnet ef migrations add MultiAppObservability
dotnet ef database update
```

- **Rolling-deploy safe.** Old-version processes ignore the new columns/tables (EF never `SELECT *`s), write `null`, and don't register instances. New-version processes write the full set. Legacy `null`-application rows read as **"(unassigned)"**.
- **Metrics reads are cross-version-safe.** The per-app metrics use a **disjoint** counter-key namespace, so the existing keys are byte-for-byte unchanged and old readers never see the new ones.

:::note Who owns migrations in a shared schema
Warp deliberately never touches the schema itself (no `Database.Migrate()`-on-boot). In a shared-database deployment, deciding *which* application runs `dotnet ef migrations add / database update` against the shared schema is a pre-existing operational concern — this feature doesn't change it. Coordinate schema migrations through whichever app you already designate as the schema owner.
:::

## Related

- [Outbound Adapters](./adapters.md) — outbound call rows carry the producing `Application`, with per-app adapter metrics.
- [Inbound Endpoint Observability](./endpoint-observability.md) — inbound request rows carry the producing `Application`, with per-app endpoint metrics and application as part of endpoint identity.
- [Outbound Webhooks](./webhooks.md) — delivery rows carry the producing `Application`.
