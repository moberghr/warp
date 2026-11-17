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

:::note[A deliberate contract change]
`AddWarp` has historically been passive — it registers services and never starts a background loop. When `ApplicationName` is set, it now runs this one lightweight heartbeat host. This is intentional and narrowly scoped: gated on `ApplicationName`, no provider or lock required, and it deregisters cleanly on shutdown. With `ApplicationName == null` (the default) nothing starts and `AddWarp` stays passive exactly as before.
:::

Stale instances (a process that crashed without deregistering) are swept by `ExpirationCleanup` once their last heartbeat is older than `ApplicationInstanceStaleGrace`. Each reaped instance — and each stale server removed by `ServerCleanup` — emits an `InstanceDown` [operational notification](./operational-notifications.md) to any registered `IWarpNotifier`, so a host can alert on a process going down.

### Provenance on everything produced

When `ApplicationName` is set, the producing app's name is stamped as a nullable `Application` column on:

- **`Job`** — the **publishing** application (stamped at publish, preserved on requeue).
- **`AdapterCallLog`** — the app that made the outbound call.
- **`EndpointCallLog`** — the app that served the inbound request.
- **`WebhookDelivery`** — the app that sent the webhook.

Every dashboard list (Jobs, Adapters, Endpoints) gains a **global application filter** driven by these columns. Rows produced before the feature was enabled — or by an old-version process — have `Application = null` and surface under an **"(unassigned)"** bucket.

> **Limitation:** recurring-job firings carry no `Application` and — until this is revisited — are not attributed to a publishing application (a recurring definition has no owning app to inherit from), so they surface under the "(unassigned)" bucket.

### Per-application metrics

Adapters and endpoints accrue **per-application** metrics (count, duration, error rate) in addition to their existing app-agnostic totals. `Application` also becomes part of **endpoint identity**, so the same route served by two applications stays two distinct aggregates rather than collapsing into one.

### Per-job-type and per-handler execution metrics

Job execution metrics — count, average/percentile duration, and error rate — accrue **by job type** and **by handler**, tagged by the **executor** application (the app whose worker actually ran the job). These fold into the same durable `Statistic` aggregates the rest of Warp uses, so they **survive job-row cleanup**: after a completed job's row is deleted, its contribution to the type/handler counts, average latency, and error rate remains. The dashboard's Jobs-by-Type view exposes both a by-type and a by-handler slice.

### Lifecycle log

A unified `ApplicationInstanceLog` records lifecycle events for **all** instances (server and non-server): `Registered`, `HeartbeatLost`, `Recovered`, `Stopped`, `StaleSwept`. This is distinct from `ServerLog`, which stays server-task-execution history and is relabeled **"Server tasks"** in the UI.

### Deploy history

Warp detects deploys from `ApplicationVersion` without any CI integration. A **deploy** is the first instance of an application to register with a version not seen before for that application in that environment: one `ApplicationDeploy` row (application, version, environment, first-seen time, and the first instance) is written at instance registration, by the server's registration for a server and by the heartbeat host for a non-server process. Nothing is added to the worker path.

- **Exactly one record per rollout.** Ten replicas of `2.4.1` starting at once produce one deploy, timestamped at the first one. A unique index on (application, environment, version) settles the race; the instance that loses it reads its duplicate-key error as "already recorded".
- **Needs both settings.** With `ApplicationName` or `ApplicationVersion` unset, nothing is recorded.
- **Per environment.** The same version reaching `staging` and then `production` is two deploys. A process with no `ApplicationEnvironment` counts as its own environment.
- **A rollback to a version already recorded is not a new deploy.** The version is not new for the application, so the original record stands.
- **Independent of instance liveness.** The stale-instance sweep and the lifecycle-log retention never delete deploy history. It has its own retention, `ApplicationDeployRetention`, which keeps it forever by default. When you set it, the newest deploy of each application and environment is always kept, so a long-running version is not recorded again as a fresh deploy the next time one of its instances starts.
- **Best effort.** A failure to record a deploy (for example, a database not yet migrated) is logged as a warning and never fails startup. The next instance of that version to start records it.

The application detail page lists the deploys as `previous → new` per environment. The API serves them newest first:

- `GET {prefix}/api/applications/{id}/deploys?from=&to=` for one application (`{id}` is the URL-safe id, as on the other application routes).
- `GET {prefix}/api/applications/deploys?application=&from=&to=` across applications, with `application` as the application **name**.

`from` (inclusive) and `to` (exclusive) are ISO 8601 instants and either can be left out. At most 500 deploys are returned. `IApplicationDeployQueryService.GetDeploys(application, since, until)` is the same read in code. It is a separate interface from `IApplicationQueryService`, so a host that registered its own `IApplicationQueryService` before `AddWarp` keeps compiling and still gets the deploy history from the database.

### Post-deploy verdict

After a rollout, a CI/CD step or a person can ask one question and get one machine-readable answer: is release X of application A healthy?

```http
GET {prefix}/api/applications/verdict?application=orders-api&version=2.3.1&environment=production&minWindowMinutes=10&minCoverage=0.5
```

| Parameter | Required | Meaning |
|---|---|---|
| `application` | yes | The application **name** (not the URL-safe id). |
| `version` | no | The version to judge. Left out, the latest deploy is judged. |
| `environment` | no | Look only at deploys in this environment. Left out, the newest matching deploy in any environment is taken. |
| `minWindowMinutes` | no | The settle time, a whole number from 0 to 1440 (default 5). |
| `minCoverage` | no | The share of checks that must have data for a `pass` or `warn`, a number from 0 to 1 (default 0). Below it the verdict is `noData`. |
| `board` | no | The [named board](./custom-metrics.md#named-boards) whose tiles count, by **name** (case ignored). Left out, the tiles of **every** board count. SLOs count either way. |

A missing `application`, a value longer than 200 characters, a bad `minWindowMinutes` or a bad `minCoverage` is a **400**. An application with no recorded deploy, or no deploy matching the version and environment, is a **404**, and so is a `board` that names no board (`{ "error": "No metric board is named '…'." }`): judging no tiles at all would read as a pass. The route is read-only and carries the dashboard's authorization like every other route.

**Which deploy, which window.** The deploy comes from the [deploy history](#deploy-history). The verdict judges only **complete metric buckets that start after the deploy** (buckets are `FineResolutionMinutes` wide, 5 minutes by default), so no traffic from the previous release is counted:

- The window starts at the deploy's `FirstSeenAt` rounded **up** to the next bucket boundary. The bucket the deploy happened in mixes old and new traffic, so it is never counted, even when the deploy was in its first seconds.
- The window ends where the board's complete data ends: the start of the bucket holding `now − MetricFlushInterval` (30 s by default). The bucket still being written is left out.
- If a newer version has since been deployed in the same environment, the window ends at that deploy rounded **down** to a bucket boundary instead (when that is earlier): a superseded release is judged over its own lifetime.

The response names the version deployed before it (`previousVersion`) and, when superseded, the one after it (`supersededBy`). A window longer than 90 days starts 90 days before its end, the board's read limit.

**Pending.** The latest deploy is `pending` until its clean window holds `minWindowMinutes` of complete data (and at least one bucket, so `minWindowMinutes=0` still waits for one), whatever its checks say. A CI step polling right after a rollout therefore never reads a premature pass, or a fail from two minutes of data. `window.settlesAt` says when to ask again: the end of the bucket that completes the minimum, plus `MetricFlushInterval`. The checks are still returned, so a person can look early.

With the defaults (5-minute buckets, a 30 s flush interval, `minWindowMinutes=5`), a deploy at 10:00:00 settles at 10:05:30, and a deploy at 10:00:10 or at 10:04:50 settles at 10:10:30 (its window starts at 10:05). So a verdict settles between 5½ and 10½ minutes after the deploy. A `minWindowMinutes` that is not a whole number of buckets rounds up: 7 minutes waits for two 5-minute buckets.

A **superseded** deploy keeps the settled-over-its-lifetime rule: its window can never grow, so it is judged over whatever complete buckets it has, however few, and never waits for `minWindowMinutes`. `window.settlesAt` is when its last bucket is complete: the next deploy rounded down, plus the flush interval (v2 at 10:00, v3 at 10:07, a 5-minute settle time: v2 is judged over 10:00–10:05, settled at 10:05:30; a deploy replaced seconds ago can read `pending` for at most that flush interval). If no whole bucket lies between the two deploys (v2 at 10:00:10, v3 at 10:04:50), there is nothing of v2's alone to judge: the verdict is `noData`, with a summary saying it was superseded before a complete bucket, never `pending`.

**What counts.**

- **Health-board tiles** ([custom metrics](./custom-metrics.md#health-board)) of every board, or of the `board` you name, scoped to this application, plus tiles with no application, evaluated over the window against their own rule: a threshold, or a deviation from the same window 1 or 7 days earlier. Tiles without a status rule are skipped. A tile scoped to another application never counts. An unscoped tile covers every application, so on a shared database, scope the tiles that should gate one application to it.
- **SLOs** ([SLO / error budget](./slo-error-budget.md)) that are enabled and scoped to this application or to none, with their **current** evaluated state. An SLO never evaluated, or not evaluated since the window started (`LastEvaluatedAt` before `window.from`, the deploy's first complete bucket), counts as no data. That evaluation time is the only part of the clean window an SLO can follow: its state covers its own rolling window, which reaches back before the deploy, and the verdict cannot re-cut it. The evaluation history is not kept, so "healthy before, breaching after" is not computed. A superseded deploy gets no SLO checks, because their current state describes the newer release.

**Aggregation.**

| Checks | Verdict |
|---|---|
| The deploy is the latest in its environment and its clean window is not yet `minWindowMinutes` of complete data | `pending` |
| Any check is Breaching | `fail` |
| Otherwise, the share of checks with data is below `minCoverage` | `noData` |
| Otherwise, any check is Warning | `warn` |
| Otherwise, any check is Healthy or Acknowledged | `pass` |
| No checks, or every check is No data | `noData` |

**Sparse data.** A check without data never fails or warns, so with the default `minCoverage=0` a single healthy check among nine without data is a `pass`. The response says how thin that is: `checksWithData` and `checksTotal` count the checks, and the `summary` line starts with them (`pass (1 of 9 checks had data): 1 healthy, 8 no data`). A CI step that wants more evidence passes `minCoverage`, for example `0.5`: below half, a would-be `pass` or `warn` is `noData` and the summary adds `below minCoverage 0.5`. A `fail` stays a `fail` whatever the coverage, since a breaching check is evidence on its own.

**Acknowledged never fails.** A tile whose Warning or Breaching status is covered by an active acknowledged-noise [annotation](./custom-metrics.md#annotations) reads `acknowledged` and counts as a pass. An SLO reads `acknowledged` only when it is **Breaching** under an active acknowledgement: an acknowledgement silences a breach, so an acknowledged SLO in Warning stays `warning`. The check keeps `acknowledged: true` and, for a tile, the rule's own state in `unacknowledgedState`. The verdict checks for a covering acknowledged-noise note active at `window.to`, the complete-data cut, which trails now by 30 s to 5½ minutes, while the live board checks at now. A note that starts now acknowledges on the board at once, but in the verdict only once the cut passes its start: backdate the note's start to acknowledge a deploy that is already being judged.

**Response.**

```json
{
  "verdict": "warn",
  "summary": "warn (4 of 4 checks had data): 1 warning, 1 acknowledged, 2 healthy",
  "checksWithData": 4,
  "checksTotal": 4,
  "deploy": { "application": "orders-api", "version": "2.3.1", "environment": "production",
              "firstSeenAt": "2026-11-16T11:40:00Z", "instanceName": "orders-api-4b2d",
              "previousVersion": "2.3.0", "supersededBy": null },
  "window": { "from": "2026-11-16T11:40:00Z", "to": "2026-11-16T11:55:00Z",
              "minWindowSeconds": 300, "settlesAt": "2026-11-16T11:45:30Z", "settled": true },
  "checks": [
    { "source": "tile", "id": 2, "name": "Checkout errors", "application": "orders-api",
      "state": "warning", "unacknowledgedState": null, "acknowledged": false,
      "value": 412, "baseline": 340, "target": 0.25, "threshold": null, "budgetRemaining": null,
      "reason": "Warning: 412 vs 7-day baseline 340 (max rise 0.25)." },
    { "source": "slo", "id": 7, "name": "Heap ceiling", "application": "orders-api",
      "state": "healthy", "unacknowledgedState": null, "acknowledged": false,
      "value": 0.9917, "baseline": null, "target": 0.95, "threshold": 600, "budgetRemaining": 0.834,
      "reason": "Healthy: observed 99.17% compliant vs target max ≤ 600 in 95%, budget remaining 83%." }
  ]
}
```

`value` is the tile's value over the window, or the SLO's observed value (its attainment). `baseline` is a deviation tile's baseline. `target` is a threshold tile's threshold, a deviation tile's allowed deviation, or the SLO's target. A value is `null` when there is nothing to show.

**An SLO's `value` and `target` are always on the same scale**, so a script can compare them:

| SLO kind | `value` | `target` | `threshold` | Reason reads like |
|---|---|---|---|---|
| Success rate, deadline attainment, metric ratio | the good share | the required share | `null` | `observed 99.12% vs target 99.50%` |
| Metric ratio with a baseline | the drop in ratio (0–1) | the largest drop allowed | `null` | `observed a drop of 1.20 pts vs target a drop of at most 5.00 pts from the 7-day baseline` |
| Metric volume floor | the volume as a share of the baseline | the smallest share allowed | `null` | `observed 83.00% of baseline vs target ≥ 50.00% of baseline` |
| Queue-wait and execution latency | the observed percentile, ms | the limit, ms | `null` | `observed 120 ms vs target 200 ms` |
| Backlog depth, metric threshold, metric gap | the observed count | the limit | `null` | `observed 12 vs target 100` |
| Metric latency | the share of events within the threshold | the required share, `percentile / 100` | the threshold, in the instrument's unit | `observed 98.74% ≤ 2 vs target p95 ≤ 2` |
| Metric gauge threshold | the share of observed time that complied | the required share, `percentile / 100` | the threshold, in the instrument's unit | `observed 99.17% compliant vs target max ≤ 600 in 95%` |

The reason uses the SLO page's wording and gives the budget left as a whole percentage. Before this release a metric latency or gauge threshold check reported the threshold in `target` against a share in `value` (`observed 0.9917 vs target 600`); `target` is now the required share and the threshold moved to the new `threshold` field, which is `null` for every other check. The reason text is for people, not for parsing.

**Which board.** Without `board`, the verdict reads the tiles of every board, exactly as it did when there was only one board: moving a tile from the Default board to a named one never drops it from an existing CI check. Pass `board=Checkout` to gate a release on the Checkout board alone (a team's own checks); the SLOs are not on any board and still count. The board is named, not numbered, because a CI script knows the name.

**Cost.** One deploy read, one batched [board read](./custom-metrics.md#health-board) (two grouped statements at most, whatever the tile or board count), one SLO read, and with `board` one read of the board list. Nothing is read per tile or per objective. `IDeployVerdictService` is registered by `AddWarp`, so a dashboard-only host serves the route. The application detail page shows the verdict of the latest deploy above the **Deploys** card.

**Calling it from CI.** The route sits behind the dashboard's gate ([Dashboard authorization](../operations/dashboard-auth.md)). Warp adds no token scheme of its own, so use the gate you already have:

- **Your own policy.** If the dashboard is gated with `RequireAuthorization("YourPolicy")`, let that policy accept a credential your pipeline already holds. For example, add your bearer scheme to it (`policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, JwtBearerDefaults.AuthenticationScheme)`), or use the API-key policy pinned on `WarpDashboardDefaults.DenyScheme` shown under [Localhost only](../operations/dashboard-auth.md#localhost-only).
- **Built-in login.** Sign in once and reuse the cookie. `POST {prefix}/api/auth/login` takes the form fields `username` and `password`.
- **Localhost only.** Run the step on the host itself.

A CI step that waits for the deploy to settle and fails the pipeline on `fail`:

```bash
#!/usr/bin/env bash
set -euo pipefail
BASE="https://ops.example.com/warp"
APP="orders-api"; VERSION="$GIT_TAG"; ENV="production"

# Built-in login: keep the cookie in a jar. With your own policy, send its header instead (-H "Authorization: Bearer $TOKEN").
curl -fsS -c jar.txt -X POST "$BASE/api/auth/login" \
  --data-urlencode "username=$WARP_USER" --data-urlencode "password=$WARP_PASSWORD" > /dev/null

for attempt in $(seq 1 20); do
  body=$(curl -fsS -b jar.txt --get "$BASE/api/applications/verdict" \
    --data-urlencode "application=$APP" --data-urlencode "version=$VERSION" \
    --data-urlencode "environment=$ENV" --data-urlencode "minWindowMinutes=10" \
    --data-urlencode "minCoverage=0.5")
  verdict=$(jq -r .verdict <<< "$body")
  jq -r .summary <<< "$body"
  case "$verdict" in
    pending) sleep 60 ;;
    fail) jq -r '.checks[] | select(.state == "breaching") | "  \(.name): \(.reason)"' <<< "$body"; exit 1 ;;
    *) exit 0 ;;   # pass, warn and noData do not block here; tighten to taste
  esac
done
echo "Verdict still pending"; exit 1
```

A 404 right after a rollout means no instance of the new version has registered yet: deploys are recorded at registration.

**Limits.** Metric points and SLOs carry an application but not an environment, so the checks are application-wide even when `environment` picks the deploy. A rollback to a version already recorded is not a new deploy, so "the latest deploy" is the newest *new* version. Only the newest 500 deploys of the application are searched.

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
| `ApplicationDeployRetention` | `TimeSpan?` | `null` (keep forever) | Age cap for deploy history, by first-seen time. The newest deploy of each application and environment is always kept. |

## Migration and upgrade

The change is **100% additive**: two new tables (`application_instance`, `application_instance_log`) and seven new nullable columns (`server` gains `application`/`version`/`environment`; `job`, `adapter_call_log`, `endpoint_call_log`, and `webhook_delivery` each gain `application`). There are no drops, renames, retypes, or new NOT-NULL-without-default columns.

Because `WarpModelCustomizer` contributes the two entities to your model unconditionally (they are [always in the schema](./adapters.md#always-in-the-schema), like the other addon entities), your **standard migration flow** picks the delta up:

```bash
dotnet ef migrations add MultiAppObservability
dotnet ef database update
```

- **Rolling-deploy safe.** Old-version processes ignore the new columns/tables (EF never `SELECT *`s), write `null`, and don't register instances. New-version processes write the full set. Legacy `null`-application rows read as **"(unassigned)"**.
- **Deploy history** adds one more table, `application_deploy`, with a unique index on (`application`, `environment`, `version`). It is additive like the rest; until it exists, a new-version process logs a warning instead of recording its deploy.
- **Metrics reads are cross-version-safe.** The per-app metrics use a **disjoint** counter-key namespace, so the existing keys are byte-for-byte unchanged and old readers never see the new ones.

:::note[Who owns migrations in a shared schema]
Warp deliberately never touches the schema itself (no `Database.Migrate()`-on-boot). In a shared-database deployment, deciding *which* application runs `dotnet ef migrations add / database update` against the shared schema is a pre-existing operational concern — this feature doesn't change it. Coordinate schema migrations through whichever app you already designate as the schema owner.
:::

## Related

- [Outbound Adapters](./adapters.md) — outbound call rows carry the producing `Application`, with per-app adapter metrics.
- [Inbound Endpoint Observability](./endpoint-observability.md) — inbound request rows carry the producing `Application`, with per-app endpoint metrics and application as part of endpoint identity.
- [Outbound Webhooks](./webhooks.md) — delivery rows carry the producing `Application`.
