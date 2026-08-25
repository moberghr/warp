---
sidebar_position: 10
---

import Screenshot from '@site/src/components/Screenshot';

# System

`warpsys:` — records dropped by the lossy recording pipelines (adapters, endpoints, client) when their
bounded channel was full.

Like [Issues](./issues), this is **chart only**: the key is
`warpsys:records-dropped:{pipeline}:{tier}:{stamp}`, always time-bucketed, with no lifetime total.

**Nothing here is a job failure.** A non-zero value means a recording pipeline saturated and threw
diagnostics away rather than blocking the work it was recording — deliberate behaviour, but it does mean
the adapter, endpoint or client surfaces are under-reporting for that window. The Dashboard's
**Dropped (24h)** tile reads the same data, and a throttled operational event fires on the same signal.

If this is regularly non-zero the fix is upstream: record less (per-adapter `FailuresOnly`), or route the
firehose to OTel instead of the database.

<Screenshot light="/img/screenshots/45-counters-system.png" dark="/img/screenshots/45-counters-system-dark.png" alt="The System counter tab" />
