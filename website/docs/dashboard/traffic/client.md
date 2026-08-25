---
sidebar_position: 3
---

import Screenshot from '@site/src/components/Screenshot';

# Client

The **Client** page surfaces telemetry your frontend apps report through the browser ingest endpoint — errors, Core Web Vitals, logs, and custom events. It appears in the nav when [client observability](/docs/features/client-observability) is enabled.

<Screenshot light="/img/screenshots/26-client.png" dark="/img/screenshots/26-client-dark.png" alt="Client page showing error rate, Core Web Vitals p75 tiles, top errors and a recent event stream" />

At a glance:

- **Error rate** and per-type counts (errors, logs, events, vitals).
- **Core Web Vitals (p75)** — LCP, INP, CLS, FCP, TTFB, colored by Google's good / needs-improvement / poor thresholds.
- **Top errors** by frequency.
- **Recent events** — a filterable stream (All / Errors / Logs / Events / Requests / Vitals). Each row links to its **session**.

## Session timeline

Click a session to see the **unified client↔server timeline**: the browser's events (errors, logs, vitals, custom events, and the API **requests** it made) merged in order with the **server** endpoint calls those requests triggered — joined by the W3C trace id the browser propagated.

<Screenshot light="/img/screenshots/29-client-session.png" dark="/img/screenshots/29-client-session-dark.png" alt="Session timeline interleaving client events with the server endpoint calls they triggered" />

Server rows and request rows link out to the full job **trace waterfall**, so you can follow a single user action from the click, through the API call, into the jobs it spawned — client and server on one page.

See the [Client observability](/docs/features/client-observability) feature page for setup, the shipped browser script, and the ingest/PII model.
