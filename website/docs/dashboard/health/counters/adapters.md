---
sidebar_position: 6
---

import Screenshot from '@site/src/components/Screenshot';

# Adapters

`adapter:` — outbound service calls, with a row per adapter plus rows per **operation** (`op=…`) and
**group** (`grp=…`) wherever those axes were recorded.

Reading the axes against each other is the diagnosis:

- an **operation** red across every group → a caller-side bug: malformed payload, schema drift;
- a **group** red across every operation → that counterparty is down, or its token expired;
- **everything** red → the adapter or the vendor itself.

`Throttled` and `Circuit open` are separate columns from `Failed` on purpose: they are your own
backpressure working, not the dependency failing. A large circuit-open count beside a small call count
means the breaker is doing its job and most calls never left the process.

[Adapters](/docs/dashboard/traffic/adapters) renders the same data as a page, with health pills, trends
and per-call detail.

<Screenshot light="/img/screenshots/41-counters-adapters.png" dark="/img/screenshots/41-counters-adapters-dark.png" alt="The Adapters counter tab" />
