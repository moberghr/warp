---
sidebar_position: 2
---

import Screenshot from '@site/src/components/Screenshot';

# Job types

`jobstat:` keyed by job **type** — one row per published type, with executions, failures, average and p95.

Latency comes from a durable histogram folded through `Counter` → `Statistic`, so it is exact over every
execution and survives `JobLog` cleanup. It is not sampled from whatever rows happen to remain.

Type names are assembly-qualified in the data (`Acme.Orders.ProcessOrderRequest, Acme.Orders, Version=…`),
so the table shows the short name with its namespace beneath and the full string as the row tooltip.

Compare this tab with [Handlers](./handlers): for an ordinary job they agree, and where they diverge you
are looking at a routed message.

<Screenshot light="/img/screenshots/37-counters-job-types.png" dark="/img/screenshots/37-counters-job-types-dark.png" alt="The Job types counter tab" />
