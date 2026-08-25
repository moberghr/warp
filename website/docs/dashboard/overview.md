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

### The dashboard API ignores your JSON options

The dashboard's REST API (everything under `{RoutePrefix}/api`) and the bundled SPA ship together as one closed contract, so Warp pins its own response format — camelCase property names, enums as numbers — regardless of what the host process configures.

This matters because `ConfigureHttpJsonOptions` is **process-wide** for minimal APIs. Before Warp pinned its own options, a host that did the common thing:

```csharp
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
```

reshaped Warp's payloads as a side effect: `currentState` arrived as `"Failed"` instead of `5`, so the dashboard — which looks states up by number — showed **Unknown** on every state badge, dropped the Requeue/Delete buttons on job detail, and could never render the "Cancelling…" badge. A `PropertyNamingPolicy` change broke it the same way.

Nothing is required of you, and there is no setting to get wrong: configure JSON however your own API needs it. Your endpoints keep your options; the dashboard keeps its own.

:::note Inbound Warp HTTP endpoints are different

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
