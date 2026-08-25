---
sidebar_position: 5
---

import Screenshot from '@site/src/components/Screenshot';

# Deadlines

`deadline:` — `Total`-scope timeout attainment per job type: how often a job carrying a total deadline
finished inside it.

A **late completion counts as a miss**. A handler that ignores its cancellation token and finishes anyway
still missed, and counting it as a success would hide exactly the case the deadline was set to catch.

`miss` is a subset of `count` rather than a sibling of it, so this family names its count token
explicitly — otherwise the average would be computed against a denominator counting some executions twice.

A climbing miss rate usually means the deadline is now too tight for what the handler does, rather than
the handler being broken. This is the input to a
[deadline-attainment SLO](/docs/dashboard/health/slo).

<Screenshot light="/img/screenshots/40-counters-deadlines.png" dark="/img/screenshots/40-counters-deadlines-dark.png" alt="The Deadlines counter tab" />
