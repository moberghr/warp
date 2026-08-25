---
sidebar_position: 9
---

import Screenshot from '@site/src/components/Screenshot';

# Issues

`errorgroup:` — the hourly occurrence trend per error-group fingerprint.

**Trend only, and deliberately so.** The key is `errorgroup:{fingerprint}:{yyyy-MM-dd-HH}`; there is no
lifetime total, so this tab is a chart with no table beneath it. That is not missing data.

It exists because the trend is durable. Raw error rows are swept by retention, but the fold survives — so
"was this spiking last Tuesday" stays answerable long after the occurrences themselves are gone.

A fingerprint is 32 hex characters with no information in the tail, so it is truncated for display. To
find out what one *is* — title, culprit, stack sample, status — go to
[Issues](/docs/dashboard/health/issues), the page this trend belongs to.

<Screenshot light="/img/screenshots/44-counters-issues.png" dark="/img/screenshots/44-counters-issues-dark.png" alt="The Issues counter tab" />
