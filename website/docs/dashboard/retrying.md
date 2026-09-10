---
sidebar_position: 2.5
---

# Retrying

Below the Jobs state list, under a separator, sits **Retrying** — jobs whose last attempt threw and
which are waiting to run again.

## It is a filter, not a state

There is no `State.Retrying`. When a handler throws and the retry policy still has budget,
`RetryPipelineBehavior` reschedules the job into `Scheduled` — or `Enqueued`, when the retry schedule
is empty — and increments the attempt counter it keeps in `Job.Metadata`.

So every job listed here is **also** listed under the state it actually sits in. The sidebar counts
deliberately do not sum to the job total, and that is why the entry sits under a separator rather than
beside the states.

With the default retry schedule of `[15s, 60s, 300s]`, virtually every row will read `Scheduled`.

## Columns

Beyond the usual job columns, the list adds two:

| Column | Reads | Meaning |
|---|---|---|
| **Attempt** | `#3 (2 failed)` | The attempt about to run, and how many have already failed. |
| **Next attempt** | `in 5 minutes` | When the retry is scheduled to run. |

The attempt count comes from the retry bookkeeping already on the job — nothing is added to the
schema, and nothing extra is written on the worker's hot path to produce it.

A job that exhausts its retries is no longer waiting on an attempt, so it leaves this view and appears
under **Failed**. One that succeeds on a later attempt leaves it for **Completed**.

## Turning it off

The tab is shown by default:

```csharp
app.MapWarpDashboard(o => o.ShowRetries(false));
```

:::note Why a config switch rather than auto-detection
Unlike the other addon-driven pages, this one has no service to probe: retrying jobs are found by
reading `Job.Metadata`, which any dashboard can do whether or not `AddRetry()` ran in *this* process —
or in any process, since an attempt count can also arrive through the public metadata surface.

Auto-detection would also be the wrong signal. In a dashboard-only deployment the workers hold the
addons and the dashboard holds none, so the tab would be hidden over rows sitting in the database. The
operator knows what the cluster runs; the container does not. See
[Nav declarations](/docs/dashboard/overview#nav-declarations).
:::

## Cost

The view is the one place in the dashboard that reads the retry counter out of `Job.Metadata`, which
is an open dictionary in a text column that no index covers. The query is therefore a scan over the
**live backlog** — `Enqueued` plus `Scheduled` — and its cost is set by how large that backlog is, not
by how many jobs are actually retrying.

Measured on 500k jobs with an 86k backlog: **~24 ms warm, ~160 ms cold**, against 0.08 ms for the
Scheduled tab. That is fine for a page a person opens, which is why it is only ever triggered by
navigation: the sidebar badge has its own count-only route, is scoped to the Jobs section, and is
deliberately kept off both the dashboard's polling loop and its realtime invalidation.

If a deployment's backlog ever makes the page slow, the escape hatch — a Postgres partial index needing
no schema change or table rewrite — is documented with the full measurements in `docs/perf-results.md`
in the repository, along with the `jsonb` variant that was measured and rejected.
