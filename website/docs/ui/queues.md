---
sidebar_position: 11
---

import Screenshot from '@site/src/components/Screenshot';

# Queues

The **Queues** page shows the health of each queue: how long jobs wait to be picked up, and how deep the queue is backed up. It's always in the nav (queue metrics are a built-in feature).

<Screenshot light="/img/screenshots/25-queues.png" dark="/img/screenshots/25-queues-dark.png" alt="Queues page showing per-queue backlog depth, oldest age and queue-wait percentiles" />

Per queue you get:

- **Backlog** — how many eligible jobs are waiting right now, and the **Oldest** age of the frontmost one (sampled every ~60s).
- **Claimed** — how many jobs have been picked up (the queue-wait sample count).
- **Queue-wait latency** — **Avg**, **p95**, and **p99** time between a job becoming eligible and a worker claiming it.

Latency percentiles come from durable aggregates, so they stay meaningful even after old job rows are cleaned up. A rising backlog with a growing oldest-age is the classic "not enough worker capacity for this queue" signal.

See the [Queue metrics](/docs/features/queue-metrics) feature page for the meters and how the numbers are computed.
