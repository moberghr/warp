---
sidebar_position: 3
---

# Counters

Every durable metric Warp folds through `Counter` → `Statistic`, grouped by the subsystem that wrote it. Used for forensics ("what events happened, by metric, when") and addon visibility — any counter an addon writes to the `Counter` / `Statistic` tables shows up here automatically, no per-key wiring required.

## Built-in counters

Every counter is **append-only**: it records that an outcome happened, and a later requeue or delete never un-counts it. `stats:succeeded` / `failed` / `deleted` therefore mean *ever*, not *currently* — "how many are failed right now" is the Failed tile, which queries the `Job` table.

Each **state total** carries a **reason breakdown** written by the addon that caused the outcome:

| Key | Incremented when |
|---|---|
| `stats:succeeded` | A job's handler completes successfully. No reason breakdown — nothing stamps a reason on a success |
| `stats:failed` | A job ends `Failed` — plus `-retry-exhausted` (a granted retry budget was spent) and `-saga` |
| `stats:deleted` | A job ends `Deleted` — plus `-timeout`, `-concurrency` (mutex/semaphore Skip), `-ratelimit`, `-saga` |
| `stats:requeued` | A job goes back on the queue without finishing — plus `-retry`, `-concurrency` (Wait), `-ratelimit` (Wait), `-circuitbreaker`, `-saga`, `-manual` (dashboard requeue), `-recovery` (crash recovery) |
| `stats:retried-jobs` | A job enters retry for the **first** time — distinct jobs, where `requeued-retry` counts events |

Totals are written independently of their breakdown, so an outcome no addon claimed (a plain handler throw) still lands in its total; the page shows the difference as an **unattributed** row rather than letting the numbers look broken. `stats:unsuccessful` is **derived on read** as `failed + deleted` and never stored.

Each event also writes a parallel `:{yyyy-MM-dd-HH}` hourly key so the chart can break the same metric down by hour.

## The page

One **tab per counter family**, because the families measure different things in different units and reading them as one alphabetical list does not work: a per-job-type duration SUM in the hundreds of thousands of milliseconds sits next to an execution count of 2, and the dimension is an assembly-qualified type name. Only families that actually have data get a tab.

Each tab has:

**Hourly history chart** — one series per dimension, for **one metric at a time** (the metric toggle sits next to 24h / 7d). Plotting a duration sum on the same axis as a count flattens the count to zero, so they are never charted together. The ten largest series are drawn and the rest stay in the table below, which is stated on screen. Hovering shows only the series that actually moved in that hour, largest first — zeros are dropped. Built-in outcome metrics get fixed colors in family hues (succeeded green, failed reds, deleted grays, requeued ambers — breakdown keys tint their parent's hue); everything else gets a deterministic color hashed from the key so it stays the same across reloads.

**Table** — one row per dimension, with a filter box. Names are shortened for display (`ProcessOrderRequest` with `Acme.Orders` beneath it; the full assembly-qualified name is the row's tooltip). Duration sums and latency-histogram buckets are never shown as raw columns — they are folded into derived **Avg** and **p95** (p75 for web vitals) columns instead.

Hourly variants are filtered out of every table; the charts consume them separately.

import Screenshot from '@site/src/components/Screenshot';

<Screenshot light="/img/screenshots/17-counters.png" dark="/img/screenshots/17-counters-dark.png" alt="Counters" />

## The tabs

Each tab is its own route — `/counters/{family}` — so a tab can be linked to, survives a refresh, and the
back button steps through them. They measure different subjects in different units, which is why they are
separate views rather than one list.

| Tab | Measures | Keys |
|---|---|---|
| [Job outcomes](./job-outcomes) | Global outcome totals and their reason breakdown | `stats:` |
| [Job types](./job-types) | Executions and latency per published job type | `jobstat:` |
| [Handlers](./handlers) | The same, sliced by the handler that ran | `jobstat:` |
| [Queues](./queues) | Queue-wait latency and the backlog gauge | `qwait:`, `qbacklog:` |
| [Deadlines](./deadlines) | Timeout attainment per job type | `deadline:` |
| [Adapters](./adapters) | Outbound calls per adapter, operation and group | `adapter:` |
| [Endpoints](./endpoints) | Inbound calls per method and route template | `endpoint:` |
| [Client](./client) | Browser events and web vitals | `clientevent:` |
| [Issues](./issues) | Hourly trend per error-group fingerprint | `errorgroup:` |
| [System](./system) | Records dropped by the lossy pipelines | `warpsys:` |
| [Other](./other) | Anything unrecognised, including addon counters | addon-defined |

Only families that hold data get a tab. A URL naming a family with no counters still opens — it says so
rather than bouncing you elsewhere, so a shared link keeps explaining itself.

## Per-application rows

Where a metric is sliced by application, the per-app keys live under their own prefix (`jobstat-app:`,
`adapter-app:` and so on) and appear as **separate rows**, never merged into the cluster-wide one. Merging
them would double every count, since a per-app slice is a slice *of* the global figure rather than
additional work.

## Counters vs. Dashboard

These pages answer different questions:

- **Dashboard** — *what is the system doing right now*. Live state counts (Enqueued / Processing / Failed waiting in queue / etc.), realtime per-second delta chart, headline succeeded/failed history. Built around current health.
- **Counters** — *what events have happened over time*. Lifetime totals for every metric and historical breakdown of every hourly counter. Built around forensics and addon visibility.

The only data overlap is the headline `succeeded` / `failed` series appearing on both. The dashboard shows them as the operationally relevant rate; the counters page shows them as two of N series alongside everything else.

## Storage and retention

Two tables back the counters:

- **`Counter`** — write-optimized, append-only. Every event becomes a new row. Workers and command handlers write here on the hot path with no row-level contention.
- **`Statistic`** — read-optimized, one row per key. The `AggregateCounters` background task (see [Servers — Background Tasks](/docs/dashboard/health/applications#background-tasks)) periodically reads `Counter` rows, sums by key, applies the sum to the matching `Statistic` row, and deletes the consumed `Counter` rows.

Reads merge both tables (`Statistic.Value + sum(Counter.Value)`) so a counter row written milliseconds before the page loads still surfaces — no aggregation lag visible to the operator.

**Retention:**

- *Rolled-up keys* (e.g. `stats:succeeded`) are kept forever. They're lifetime totals.
- *Hourly keys* (`stats:succeeded:2026-05-07-10`) are pruned after 7 days by `ExpirationCleanup`. Both built-in and addon-defined hourly metrics are pruned with the same retention as long as the key follows the `<base>:yyyy-MM-dd-HH` convention.

## Custom counters from addons

Anything you write to the `Counter` table appears here. To add an addon-specific metric:

```csharp
context.Set<Counter>().Add(new Counter { Key = "addon:my-metric", Value = 1 });

// Optional — write a parallel hourly key if you want it on the chart.
var hourSuffix = DateTime.UtcNow.ToString("yyyy-MM-dd-HH");
context.Set<Counter>().Add(new Counter { Key = $"addon:my-metric:{hourSuffix}", Value = 1 });
```

The aggregator and cleanup handle the rest — `addon:my-metric` shows up in the **Other** tab immediately (unrecognised keys are rendered raw rather than dropped), the hourly variant gets graphed and pruned at 7 days. Use `+1` / `−1` deltas (the column is signed) and let the aggregator sum them.
