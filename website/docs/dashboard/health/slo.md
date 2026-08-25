---
sidebar_position: 2
---

import Screenshot from '@site/src/components/Screenshot';

# SLOs

Service-level objectives with error-budget and burn-rate tracking. One row per objective:
**Objective**, **Scope**, **Target**, **Observed**, **Budget**, **Burn (fast / slow)** and **State**.

<Screenshot light="/img/screenshots/35-slo.png" dark="/img/screenshots/35-slo-dark.png" alt="SLO page listing objectives with target, observed value, remaining budget, burn rate and state" />

The nav entry appears once `opt.AddSlo(...)` is registered.

## Reading a row

- **Scope** is the dimension the objective watches — a queue, a job type, or `*` for everything.
- **Observed** is what actually happened over the objective's window, read from the same durable
  aggregates the Counters page uses.
- **Budget** is how much of your allowed failure remains. Negative means you've spent more than the
  objective permits.
- **Burn (fast / slow)** is the multi-window burn rate: the short window catches a sudden outage, the long
  one catches a slow bleed. Sustained burn above 1× exhausts the budget before the window closes.

## States

| State | Meaning |
|---|---|
| **Healthy** | Attainment above target, budget intact |
| **Warning** | Burning faster than sustainable |
| **Breaching** | Target missed |
| **Acknowledged** | Breaching, but silenced until the ack expires |
| **NoData** | Nothing matched the dimension in the window |

**NoData is deliberately not Healthy.** A typo'd queue or job type reads as "we are measuring nothing"
rather than a false green, and never alerts.

## Objective kinds

Five, all evaluated from aggregates already being folded — nothing new is measured on the worker hot path:

- **Success rate** — completed vs failed.
- **Queue-wait latency** — how long jobs sit unclaimed.
- **Execution latency** — how long handlers take.
- **Backlog depth** — a threshold on queue depth.
- **Deadline attainment** — how often a job with a total-scope timeout finished inside it. A *late
  completion* counts as a miss: a handler that ignores its cancellation token and finishes anyway still
  missed the deadline.

Latency objectives read a windowed percentile histogram whose buckets reach 30s, 60s and 300s — job
execution and queue-wait run well past HTTP timescales, so a 30s target observes properly instead of
saturating at the top bucket.

## Alerting

A healthy → breaching edge fires once through the operational-notifier seam. Acknowledging suppresses it
until the ack expires. Warp ships no channel integrations — register an `IWarpNotifier` and route it
wherever you page.

## See also

- [SLO and error budget](/docs/features/slo-error-budget) — defining objectives, burn-rate windows,
  evaluation cadence.
- [Counters](/docs/dashboard/health/counters) — the aggregates objectives are computed from.
