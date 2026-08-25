---
sidebar_position: 11
---

import Screenshot from '@site/src/components/Screenshot';

# Other

Keys the page does not recognise, rendered raw as key and value.

This is the escape hatch that makes the Counters page useful to addons: **any** counter written to the
`Counter` / `Statistic` tables shows up here with no per-key wiring, no registration and no schema change.
Write `acme:invoices:exported` from your own code and it appears.

Rows are shown exactly as stored, because the page has no way to know their units, their dimension, or
which tokens belong together. Once a family *is* recognised it moves to its own tab and gains the folded
Avg and percentile columns.

It is also where a **typo** surfaces. A key meant to match an existing family but written with the wrong
prefix or separator lands here rather than silently vanishing.

<Screenshot light="/img/screenshots/46-counters-other.png" dark="/img/screenshots/46-counters-other-dark.png" alt="The Other counter tab" />
