---
sidebar_position: 1
---

# Overview

Warp ships with a built-in web dashboard for monitoring and managing jobs.

## Setup

```csharp
app.MapWarpDashboard("/warp");
```

To restrict access to the dashboard, see [Dashboard Auth](/docs/operations/dashboard-auth).

### Branding

When you run the same dashboard across several environments, brand it so operators can tell them apart and jump back to your own portal:

```csharp
app.MapWarpDashboard(o =>
{
    o.BrandName = "Acme Jobs";               // replaces the "Warp" wordmark + names the browser tab
    o.InstanceName = "Production";           // shown in the header + browser tab title
    o.LogoUrl = "/img/acme-logo.svg";        // header logo
    o.PortalUrl = "https://portal.acme.com"; // back-link target
    o.PortalLabel = "Back to Acme";          // link text (defaults to "Back to app")
});
```

All five are optional. `BrandName` still names the browser tab when `LogoUrl` replaces the wordmark, so a tab reads `Acme Jobs · Production` rather than `Warp · Production`. Values are injected into the SPA as JSON-encoded runtime config, so a stray quote or markup in a branding string can't break the page.

### Menu layout

The nav bar ships with its own grouping (Workloads, Traffic, Runtime, Health). Override it when a deployment leads with different pages, and name each page from the `WarpDashboardPage` enum:

```csharp
app.MapWarpDashboard(o => o.ConfigureMenu(m => m
    .Pages(WarpDashboardPage.Dashboard, WarpDashboardPage.Jobs)
    .Divider()
    .Group("Delivery", WarpDashboardPage.Webhooks, WarpDashboardPage.Adapters)
    .Group("Health", WarpDashboardPage.Issues, WarpDashboardPage.Slo)));
```

**The bar is one ordered sequence.** `Pages`, `Group` and `Divider` all append to it, and it renders left to right exactly as declared — so a page can sit between two groups, and nothing is pinned to either end:

```csharp
o.ConfigureMenu(m => m
    .Pages(WarpDashboardPage.Dashboard)
    .Group("Ops", WarpDashboardPage.Recurring, WarpDashboardPage.Issues)
    .Divider()
    .Pages(WarpDashboardPage.Jobs));          // renders after the Ops dropdown
```

Because everything appends, calls may be split across helpers and interleaved freely, and `ConfigureMenu` itself accumulates across calls rather than replacing what an earlier one declared.

**A page you leave out is not hidden.** Everything the layout doesn't place collects in one trailing group (`OverflowLabel`, default "More"), in the order the built-in nav declares it. So a partial layout is always safe, and a page added by a later Warp upgrade shows up in the overflow rather than disappearing because your layout predates it. Declare nothing but `Pages(...)` and you get exactly that — your pages up front, one dropdown holding the rest:

```csharp
app.MapWarpDashboard(o => o.ConfigureMenu(m => m.Pages(
    WarpDashboardPage.Dashboard,
    WarpDashboardPage.Jobs,
    WarpDashboardPage.Issues,
    WarpDashboardPage.Recurring,
    WarpDashboardPage.Applications)));
```

Three rules are enforced at startup rather than silently: a page may appear in only one place, a group label may be declared only once, and no group may take the overflow group's own label (the nav keys its open dropdown on the label, so two groups sharing one would leave the overflow's pages unreachable). The first two throw from the offending call; the label collision is checked by `MapWarpDashboard`, so it catches an `OverflowLabel` set after the group too.

Addon gating is unchanged and runs after your layout: a page whose addon this process didn't register is dropped wherever you put it, and a group left empty by that gets no trigger. A divider that gating strands — leading, trailing, or beside another — is dropped too, so a rule can never dangle off the end of the bar. Extension pages keep their own slot after the declared entries; their labels are host-supplied and have no enum member to name them by.

:::note
The layout is a rendering concern only. It doesn't gate access — every page is still routable by URL, and the command palette (`Ctrl`/`Cmd`+`K`) reaches all of them regardless of grouping. Use [Dashboard Auth](/docs/operations/dashboard-auth) to actually restrict the dashboard.
:::

### The dashboard API ignores your JSON options

The dashboard's REST API (everything under `{RoutePrefix}/api`) and the bundled SPA ship together as one closed contract, so Warp pins its own response format — camelCase property names, enums as numbers — regardless of what the host process configures.

This matters because `ConfigureHttpJsonOptions` is **process-wide** for minimal APIs. Before Warp pinned its own options, a host that did the common thing:

```csharp
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
```

reshaped Warp's payloads as a side effect: `currentState` arrived as `"Failed"` instead of `5`, so the dashboard — which looks states up by number — showed **Unknown** on every state badge, dropped the Requeue/Delete buttons on job detail, and could never render the "Cancelling…" badge. A `PropertyNamingPolicy` change broke it the same way.

Nothing is required of you, and there is no setting to get wrong: configure JSON however your own API needs it. Your endpoints keep your options; the dashboard keeps its own.

:::note[Inbound Warp HTTP endpoints are different]

[`Warp.Http`](/docs/features/http) exposes **your** handlers as **your** public API, so those endpoints deliberately keep honouring your `ConfigureHttpJsonOptions` — your callers see the format you chose. Only Warp's own dashboard API is pinned.

:::

## Dashboard

The main dashboard shows real-time statistics, live graphs, and server status.

### Metric Cards

Six clickable metric cards are displayed at the top of the dashboard:

- **Enqueued** — jobs waiting to be picked up
- **Processing** — jobs currently being executed
- **Scheduled** — jobs scheduled for future execution
- **Failed** — jobs that have failed
- **Messages** — pub/sub messages
- **Batches** — batch groups

Each card navigates to its corresponding page when clicked. Cards use conditional colors: **Processing** turns purple when the count is greater than zero, and **Failed** turns red when the count is greater than zero. All other cards use neutral styling.

### Graphs

Below the metric cards, the dashboard includes two graphs:

- **Realtime graph** — a live jobs/sec line chart that updates continuously
- **Historical graph** — a bar chart with a 24-hour / 7-day toggle showing succeeded and failed job counts over time

import Screenshot from '@site/src/components/Screenshot';

<Screenshot
  light="/img/screenshots/01-dashboard.png"
  dark="/img/screenshots/01-dashboard-dark.png"
  alt="Dashboard"
/>

## Timestamps and countdowns

Every timestamp in the dashboard is rendered as an exact instant plus a relative label —
`2026-05-25 13:10:42 (5 minutes ago)` — and the relative half **updates once a second** while the
tab is visible, so a page left open does not sit on a stale "5 minutes ago". The ticking pauses
while the tab is backgrounded and catches up the moment you switch back to it. Hovering a timestamp
reveals the full instant down to the millisecond.

Columns that point at something still being waited on — a scheduled job's **Scheduled**, a retrying
job's **Next attempt**, a webhook delivery's **Next attempt**, a recurring job's **Next execution** —
count down instead:

- **`in 2 minutes`** — still ahead.
- **`due now`** — the instant has passed and the work is waiting to be picked up. This is the normal
  path, not a fault: a scheduled job becomes eligible on the `ScheduledJobActivation` cadence
  (10 seconds by default) and then waits for a worker to claim it. The label deliberately does not
  flip to "3 seconds ago", which would read as a run that already happened.
- **`overdue by 5 minutes`** — past due by more than 30 seconds, which usually means something is
  not running: a stopped server, a drained worker pool, a paused queue.

When a countdown reaches zero the page refetches, so the row moves to its new state on its own.

:::note
Relative labels are computed from the **browser's** clock. A workstation whose clock is minutes off
from the servers will show every label shifted by that much.
:::

### Status dots

A server's status dot and its **Inactive** badge follow the same clock. Green means the server has
checked in within the last 30 seconds (six missed heartbeats at the default 5-second
`HealthCheckInterval`), amber means paused, red means silent — and the dot turns red on its own
while you are watching the page, without waiting for a refresh.

The **Applications** roster answers the same question from the server instead: an instance is live
until its heartbeat is older than `ApplicationInstanceStaleGrace` (2 minutes by default), which is
also when the row is swept and an `InstanceDown` notification fires. Those pages refresh every 15
seconds to pick up the answer, so a dot there can trail a dead process by that much. Its fallback
flat server list — what you see when no `ApplicationName` is set — has no API answer to read and
uses the 30-second rule, refreshed on the same cadence.
