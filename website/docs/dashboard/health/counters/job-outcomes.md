---
sidebar_position: 1
---

import Screenshot from '@site/src/components/Screenshot';

# Job outcomes

`stats:` — global job-outcome totals and the per-reason breakdown beneath each one. The only tab rendered
as a **hierarchy** rather than a row per dimension.

Every key here is **append-only**: it records that an outcome *happened*, and a later requeue or delete
never un-counts it. So `succeeded` / `failed` / `deleted` mean *ever*, not *currently* — "how many are
failed right now" is the Failed tile on the Dashboard, which queries the `Job` table. A deployment that
requeues heavily will see these grow past the number of jobs it has ever had.

## Reading the hierarchy

- **`unsuccessful`** is **derived on read** as `failed + deleted`, never stored. A stored version was built
  and reverted: it was maintained at two of the eight sites that move `failed` / `deleted`, so it
  under-reported from the first delete and drew a child larger than its parent.
- Each **state total** sits above its **reason rows** — `failed-retry-exhausted`, `deleted-timeout`,
  `requeued-ratelimit` and so on, written by whichever addon caused the outcome.
- Totals are written **independently** of their breakdown, so an outcome no addon claimed (a plain handler
  throw) still lands in its total. The difference shows as an **unattributed** row rather than the numbers
  quietly failing to add up — and a loud `over-attributed` row for the impossible opposite direction.

`retried-jobs` counts **distinct jobs** that entered retry at least once, where `requeued-retry` counts
retry *events*. The gap between the two tells you how much of your retry volume is a few jobs failing over
and over.

<Screenshot light="/img/screenshots/36-counters-job-outcomes.png" dark="/img/screenshots/36-counters-job-outcomes-dark.png" alt="The Job outcomes counter tab" />
