---
sidebar_position: 9
---

# Counters

Raw view of every counter row in the database. Used for forensics ("what events happened, by metric, when") and addon visibility — any counter an addon writes to the `Counter` / `Statistic` tables shows up here automatically, no per-key wiring required.

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

Two sections, polled every 5s:

**Hourly history chart** — every hourly counter is its own series. Toggle 24h / 7d. Click a legend entry to hide that series. Built-in metrics get fixed colors in family hues (succeeded green, failed reds, deleted grays, requeued ambers — breakdown keys tint their parent's hue); addon-defined keys get a deterministic color hashed from the key name so it stays the same across reloads.

**Outcomes table** — the lifetime totals rendered as the hierarchy above: the derived `unsuccessful` umbrella over `failed` and `deleted`, each state total over its reason rows, with the unattributed remainder (muted) when a total exceeds the sum of its reasons — and a loud `over-attributed` row for the impossible opposite direction. Keys the hierarchy doesn't claim (addon-defined counters) follow in a flat **Other** table, sorted alphabetically. Hourly variants are filtered out of both; the chart consumes them separately.

import Screenshot from '@site/src/components/Screenshot';

<Screenshot light="/img/screenshots/17-counters.png" dark="/img/screenshots/17-counters-dark.png" alt="Counters" />

## Counters vs. Dashboard

These pages answer different questions:

- **Dashboard** — *what is the system doing right now*. Live state counts (Enqueued / Processing / Failed waiting in queue / etc.), realtime per-second delta chart, headline succeeded/failed history. Built around current health.
- **Counters** — *what events have happened over time*. Lifetime totals for every metric and historical breakdown of every hourly counter. Built around forensics and addon visibility.

The only data overlap is the headline `succeeded` / `failed` series appearing on both. The dashboard shows them as the operationally relevant rate; the counters page shows them as two of N series alongside everything else.

## Storage and retention

Two tables back the counters:

- **`Counter`** — write-optimized, append-only. Every event becomes a new row. Workers and command handlers write here on the hot path with no row-level contention.
- **`Statistic`** — read-optimized, one row per key. The `AggregateCounters` background task (see [Servers — Background Tasks](/docs/ui/servers#background-tasks)) periodically reads `Counter` rows, sums by key, applies the sum to the matching `Statistic` row, and deletes the consumed `Counter` rows.

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

The aggregator and cleanup handle the rest — `addon:my-metric` shows up in the table immediately, the hourly variant gets graphed and pruned at 7 days. Use `+1` / `−1` deltas (the column is signed) and let the aggregator sum them.
