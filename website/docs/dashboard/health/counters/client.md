---
sidebar_position: 8
---

import Screenshot from '@site/src/components/Screenshot';

# Client

`clientevent:` — browser events reported by the shipped client script, plus web-vital measurements.

Rows come in three shapes: `type …` totals per event type, `error …` / `event …` per name, and the vitals
by name.

**Vitals report p75**, the percentile Core Web Vitals is scored on — not the p95 every other tab uses.

**CLS is unitless.** It is folded ×1000 to fit the shared integer histogram and unscaled on read, so it
renders as a score (`0.07`) rather than a duration. Every other vital genuinely is milliseconds.

Names are guarded, because this data arrives at a **public ingest endpoint** and nothing a browser sends is
trusted to be bounded: vital names go through a fixed allowlist, and error/event names collapse into
`{other}` past a cardinality cap. The raw name is kept on the event row itself — only the counter key
collapses.

See [Client](/docs/dashboard/traffic/client) for sessions and individual events.

<Screenshot light="/img/screenshots/43-counters-client.png" dark="/img/screenshots/43-counters-client-dark.png" alt="The Client counter tab" />
