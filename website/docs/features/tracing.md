---
sidebar_position: 7
---

# Job Tracing

Warp automatically tracks the flow of jobs across handlers. When a job handler spawns new jobs, they share a `TraceId`, making the full execution chain visible in the dashboard.

## How It Works

Every job gets two trace fields:

- **TraceId** — All related jobs share this ID. The first job in a flow creates it (`TraceId = own ID`). All spawned jobs inherit it.
- **SpawnedByJobId** — Direct "who created me" link.

This happens automatically via `AsyncLocal` context. When a handler calls `publisher.Enqueue()` or `batchPublisher.StartNew()`, the new jobs inherit the trace.

## Example

```csharp
public class ProcessOrderHandler : IJobHandler<ProcessOrderRequest>
{
    private readonly IBatchPublisher _batchPublisher;

    public async Task HandleAsync(ProcessOrderRequest message, CancellationToken ct)
    {
        // These jobs automatically inherit the trace from ProcessOrderRequest
        var shipItems = items.Select(i => new ShipItemRequest { ItemId = i.Id }).ToList();
        var batchId = await _batchPublisher.StartNew(shipItems);

        // Continuation also inherits the trace
        await _batchPublisher.ContinueBatchWith(
            new List<SendInvoiceRequest> { new() { OrderId = message.OrderId } },
            batchId);
    }
}
```

The dashboard shows the full trace:

import Screenshot from '@site/src/components/Screenshot';

<Screenshot light="/img/screenshots/03-job-detail-trace.png" dark="/img/screenshots/03-job-detail-trace-dark.png" alt="Job detail with trace" />

The "Trace (9 jobs)" card shows all jobs spawned from this ProcessOrderRequest: 6 ShipItemRequests and 2 PublishInvoiceRequests. Clicking any job navigates to its detail, which shows the same trace from that job's perspective.

Clicking the trace link opens a dedicated visualization page showing the full DAG:

<Screenshot light="/img/screenshots/12-trace.png" dark="/img/screenshots/12-trace-dark.png" alt="Trace visualization" />

## Message-Routed Jobs

When a message is routed to multiple handlers, all resulting jobs share a `TraceId`:

```csharp
await publisher.Publish(new OrderNotification()); // Routes to EmailHandler + SlackHandler
// Both jobs get the same TraceId
```

## Unified trace view

The dashboard's `/trace/{traceId}` page shows **everything that happened for one trace** on a single screen — not just the jobs, but the browser request, the server endpoint it hit, the jobs that endpoint spawned, and the outbound adapter calls those jobs made.

The insight is that **Warp already stores spans**. Each of these rows carries a trace id, a start time, and (mostly) a duration:

| Source | Row | Is a span because… |
|---|---|---|
| Client | `ClientEventLog` (`Type = Request`) | trace id + timestamp + client-measured duration |
| Server | `EndpointCallLog` | trace id + timestamp + duration + outcome |
| Job | `Job` | trace id + create time + terminal state (parent = `SpawnedByJobId`) |
| Outbound | `AdapterCallLog` | trace id + timestamp + duration + outcome |

So the unified trace view is built by **unioning those existing rows on their shared trace id** — there is no separate span table, no trace collector, and nothing added to the worker hot path. It's a local, DB-backed view over data Warp persists anyway.

The page renders two complementary things:

- A **waterfall** (Gantt) across all four sources on one shared time axis — client (green) → server (slate) → jobs (blue) → outbound (purple) — with error bars highlighted and each span linking to its own detail page. This is the new cross-source timeline.
- The existing **job graph** (the parent/child DAG below the waterfall), which shows how jobs spawned each other.

Job bars currently show a placeholder duration (`—`): the `Job` row records when a job was created but not a clean execution duration, so only endpoint / adapter / client spans have precise bar widths today. The waterfall is reachable from the [session timeline](./client-observability.md) and from any job, endpoint, or adapter detail page.

Served by `GET {prefix}/api/traces/{traceId}` (distinct from the job-DAG endpoint that backs the graph). Because it reads local rows, it gives you a near-Jaeger experience on your own database; when your data outgrows the DB, point an external collector at Warp's always-on OTel spans (below) instead.

## OpenTelemetry Integration

Warp produces OTel-standard distributed traces and metrics using `System.Diagnostics`. Everything is on by default with zero configuration.

### Distributed Tracing

Every job execution creates a `System.Diagnostics.Activity` with:

- **TraceId** — matches the job's database `TraceId`
- **SpanId** — unique per execution (new SpanId on retries)
- **ParentSpanId** — the SpanId of whoever enqueued this job (HTTP request, another handler, etc.)

This creates a proper trace tree across job chains:

```
HTTP Request (TraceId: T, SpanId: A)
  └── Enqueue(ProcessOrder)      → Activity(TraceId: T, SpanId: B, ParentId: A)
       └── Enqueue(ShipItem)     → Activity(TraceId: T, SpanId: C, ParentId: B)
            └── Enqueue(Notify)  → Activity(TraceId: T, SpanId: D, ParentId: C)
```

Trace context is automatically propagated:
- When a handler calls `publisher.Enqueue()`, the child job captures the handler's SpanId
- When a message is routed to multiple handlers, all child jobs inherit the publisher's span
- Batch children inherit the same parent span

### Log Correlation

`AddWarpServer` automatically configures `ActivityTrackingOptions` so TraceId, SpanId, and ParentId appear in your log output:

```
info: MyApp.Handlers.SendReport[0]
      => SpanId:b7ad6b7169203331, TraceId:550e8400e29b41d4a716446655440000, ParentId:a1b2c3d4e5f60718
      Sending report to user 42
```

No configuration needed — this works with the built-in console logger and any provider that supports scopes.

### Span Attributes

Each job execution span includes these tags:

| Attribute | Example | Description |
|-----------|---------|-------------|
| `messaging.system` | `"warp"` | OTel semantic convention |
| `messaging.operation.name` | `"process"` | OTel semantic convention |
| `messaging.destination.name` | `"default"` | Queue the job belongs to |
| `messaging.message.id` | `"550e8400-..."` | Job ID |
| `warp.job.type` | `"MyApp.SendReport"` | .NET type name |
| `warp.job.kind` | `"Job"` | Job, Message, or Batch |
| `warp.job.status` | `"succeeded"` | Set after execution: `succeeded`, `failed`, `retried`, `cancelled` |
| `warp.job.duration_ms` | `142.5` | Handler execution time (on success) |
| `warp.job.retry_count` | `2` | Current retry count (only if retried) |

On failure, `Activity.SetStatus(Error)` is called with the exception message.

### Span Events

Key lifecycle moments are recorded as events on the span:

| Event | When | Attributes |
|-------|------|------------|
| `warp.job.completed` | Handler succeeds | `duration_ms` |
| `warp.job.failed` | Handler throws (no retries left) | `exception.type`, `exception.message` |
| `warp.job.retried` | Handler throws (will retry) | `retry_count`, `max_retries` |
| `warp.job.cancelled` | Job cancelled while running | — |

### Metrics

Warp exposes four metrics through a `System.Diagnostics.Metrics.Meter` named `"Warp"`:

| Metric | Type | Unit | Tags | Description |
|--------|------|------|------|-------------|
| `warp.job.duration` | Histogram | `ms` | `queue`, `type`, `status` | Handler execution time |
| `warp.job.active` | UpDownCounter | `{job}` | `queue` | Currently processing jobs |
| `warp.job.completed` | Counter | `{job}` | `queue`, `type`, `status` | Jobs that finished processing |
| `warp.job.enqueued` | Counter | `{job}` | `queue`, `kind` | Jobs enqueued |

The `status` tag is one of: `succeeded`, `failed`, `retried`, `cancelled`.
The `kind` tag is one of: `job`, `message`, `batch`.

### Exporting to OTel Backends

To export traces and metrics to Jaeger, Prometheus, Datadog, etc., subscribe to the `"Warp"` source and meter:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("Warp"))
    .WithMetrics(m => m.AddMeter("Warp"));
```

Without this, traces still appear in logs (via ActivityTrackingOptions) and metric calls are silent no-ops — no overhead.
