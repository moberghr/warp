---
sidebar_position: 1
---

import Screenshot from '@site/src/components/Screenshot';

# Issues

Errors grouped by fingerprint — one row per real problem, not per occurrence. Four surfaces feed it:
failed jobs, endpoint responses, outbound adapter calls, and browser errors reported by the client script.

<Screenshot light="/img/screenshots/31-issues.png" dark="/img/screenshots/31-issues-dark.png" alt="Issues list with source badges, event counts, trend sparklines and status chips" />

The list gives **Issue**, **Source**, **Events**, **Last seen** and **Status**, with filters for source,
status and application. Always shown — issue grouping is a Core feature, not an addon.

## How things group

The fingerprint is `hash(source + exception type + locus)`:

- The **message is normalised out of identity** — digits, GUIDs, hex and quoted literals collapse to
  placeholders — so `Order 123 not found` and `Order 456 not found` are one issue, not two. The normalised
  message becomes the title, which is also what makes the title PII-safe.
- **Stack-bearing sources** (jobs, client) group on the top in-app stack frame, so two bugs in one handler
  stay two issues.
- **Source is part of identity**, so a browser `TypeError` never merges with a server exception.

Beyond a per-source cap (2000 by default) new groups collapse into an `{other}` bucket rather than growing
without bound — the client source is fed by a public endpoint, so this guard matters.

## What counts

| Source | Counted |
|---|---|
| Jobs | Every caught exception — retry attempts *and* terminal, so a flaky handler is visible |
| Endpoints | 5xx and unhandled exceptions, plus 4xx as separate status-code groups (filtered out by default, and kept off the error-rate SLI) |
| Adapters | `Failed` only — throttling and open circuits are expected backpressure, not defects |
| Client | Browser errors only |

## Lifecycle

<Screenshot light="/img/screenshots/32-issue-detail.png" dark="/img/screenshots/32-issue-detail-dark.png" alt="Issue detail with the raw sample stack, hourly trend, recent occurrences and resolve controls" />

**Resolve** or **Ignore** an issue from its detail page. A resolved issue that recurs **regresses** back to
unresolved and gets a badge — but only for an occurrence newer than the moment you resolved it, so a
backlog of already-queued occurrences can't falsely reopen it. Ignored issues keep counting and stay
hidden. A regression can also notify through the operational-notifier seam.

A **new** issue is only a badge, never an alert: a fresh deploy mints many new types at once and paging on
each would be noise.

The detail page carries the raw sample stack, an hourly trend, the versions the issue was first and last
seen in, and a walkable list of recent occurrences — each linking into its full trace.

## See also

- [Error grouping](/docs/features/error-grouping) — fingerprinting, the inbox drain, retention and config.
- [Trace](/docs/dashboard/trace) — where an issue's sample occurrence leads.
