---
sidebar_position: 1
---

# Overview

Warp ships with a built-in web dashboard for monitoring and managing jobs.

## Setup

```csharp
app.MapWarpUI("/warp");
```

To restrict access to the dashboard, see [Dashboard Auth](/docs/operations/dashboard-auth).

### Branding

When you run the same dashboard across several environments, brand it so operators can tell them apart and jump back to your own portal:

```csharp
app.MapWarpUI(o =>
{
    o.InstanceName = "Production";           // shown in the header + browser tab title
    o.LogoUrl = "/img/acme-logo.svg";        // header logo
    o.PortalUrl = "https://portal.acme.com"; // back-link target
    o.PortalLabel = "Back to Acme";          // link text (defaults to "Back to app")
});
```

All four are optional. Values are injected into the SPA as JSON-encoded runtime config, so a stray quote or markup in a branding string can't break the page.

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
