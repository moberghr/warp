---
sidebar_position: 7
---

import Screenshot from '@site/src/components/Screenshot';

# Endpoints

`endpoint:` — inbound calls to Warp HTTP endpoints, keyed by method and route **template**
(`GET /orders/{id}`). Never the resolved path, so a path parameter cannot turn one endpoint into thousands
of rows.

`Failed` means the final wire status was 5xx, or an exception escaped after the response had started. A
4xx is a client error and stays out of it, so the error rate keeps meaning "we broke it". 4xx responses
still form [issues](/docs/dashboard/health/issues) — they just are not error-rate ones.

Latency is the durable histogram again, so p95 survives call-log retention sweeping the raw rows away.

[Endpoints](/docs/dashboard/traffic/endpoints) is the fuller view, with per-caller breakdowns and captured
request and response detail.

<Screenshot light="/img/screenshots/42-counters-endpoints.png" dark="/img/screenshots/42-counters-endpoints-dark.png" alt="The Endpoints counter tab" />
