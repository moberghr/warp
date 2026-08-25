---
sidebar_position: 4
---

import Screenshot from '@site/src/components/Screenshot';

# Queues

`qwait:` and `qbacklog:` — per-queue health. This had its own page until 6.0; the data was always these
same two folds, so it now sits beside every other durable metric.

| Column | Means |
|---|---|
| **Count** | Jobs claimed — the queue-wait sample count |
| **Backlog** | Eligible jobs waiting right now |
| **Oldest** | Age of the frontmost waiting job |
| **Avg / p95 / p99** | Time between a job becoming eligible and a worker claiming it |

Two things make this tab unlike the others. It is the only family reporting **two percentiles**, because a
queue is judged on its tail rather than its middle. And **Backlog** / **Oldest** are a *gauge* sampled
every ~60s rather than a fold — which is why the family names its count token explicitly, so counting the
gauge as an observation cannot deflate the average wait.

A rising backlog with a growing oldest-age is the classic "not enough worker capacity for this queue"
signal. Wait is measured from the moment a job became *eligible*, so a requeue restarts the clock.

See [Queue metrics](/docs/features/queue-metrics) for how the numbers are produced.

<Screenshot light="/img/screenshots/39-counters-queues.png" dark="/img/screenshots/39-counters-queues-dark.png" alt="The Queues counter tab" />
