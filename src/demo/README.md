# Warp demo (Aspire)

A one-command, runnable demo of Warp's **outbound adapters** and **durable webhooks**, orchestrated with
.NET Aspire. It stands up four things together:

| Resource | What it is |
|---|---|
| `postgres` | A Postgres container Aspire provisions on the fixed local port **5442**; its connection string is injected into the apps. |
| `partner-api` | An **external** service the demo calls — a vendor API (`/api/orders`, `/api/payments`, `/api/inventory`) **and** a webhook receiver (`/partner/webhooks*`) that verifies Standard Webhooks signatures. |
| `warp-app` | The Warp dashboard app. Registers two outbound adapters (`partner-http`, `partner-refit`) and `AddWebhooks`, runs the worker, and serves `/warp` + the seed endpoints. |
| `warp-worker` | A second Warp server on the same database — shows multi-host job processing. |

This is a **separate solution** (`Warp.Demo.slnx`) so the shipping `Warp.slnx` and CI never pull in Aspire.

## Run it

```bash
# from the repo root
dotnet run --project src/demo/Warp.Demo.AppHost
```

Requirements: the .NET 10 SDK and a container runtime (Docker Desktop / Podman) for the Postgres
container. The Aspire dashboard opens automatically; from it you can open each resource's endpoints and
watch logs and traces.

## See the features in action

1. Open the **`warp-app`** endpoint from the Aspire dashboard, then go to **`/warp`** (login `admin` / `admin`).
2. `POST /seed/adapters` on `warp-app` — enqueues jobs that call the partner API through both adapters.
   - **`/warp/adapters`** now shows `partner-http` (operations `GetOrder` healthy, `CapturePayment` with a
     real error rate, per-**Region** groups, captured request/response bodies on the failed payments) and
     `partner-refit` (`GetInventory`, slow → visible latency).
3. `POST /seed/webhooks` on `warp-app` — dispatches durable webhooks to the partner's three receiver paths.
   - **`/warp/webhooks`** shows deliveries settling: **Delivered** (reliable), retry-then-settle
     (unstable), and **Exhausted** (gone) with the host exhausted-callback logged. Each delivery's attempt
     timeline is the adapter call log keyed by the delivery id.
   - Open the **`partner-api`** `/partner` page to watch the receiver log signed webhooks arrive live.
4. The Aspire dashboard's **Traces** view shows the same adapter `Client` spans and `warp.adapter.*` /
   `warp.webhooks.*` meters (exported via OpenTelemetry), alongside the Warp dashboard's own pages.

`curl` equivalents (replace the port with the `warp-app` URL from the Aspire dashboard):

```bash
curl -X POST http://localhost:<warp-app-port>/seed/adapters
curl -X POST http://localhost:<warp-app-port>/seed/webhooks
```

## How it's wired

- **Adapters** — `warp-app` calls `opt.AddAdapter("partner-http", …)` (typed HTTP client, resilience, and
  the cluster-shared rate limiter) and `opt.AddAdapter<IPartnerApi>("partner-refit", …)` (Refit). The
  jobs live in `Warp.Test.Shared/Adapters/PartnerIntegration.cs`; the registration is in
  `Warp.TestApp/Program.cs`. Partner jobs run on a dedicated `partner` queue only `warp-app` polls.
- **Webhooks** — `opt.AddWebhooks(w => w.OnDeliveryExhausted<DemoWebhookExhaustedHandler>())`. Deliveries
  are sent both from an endpoint (`/seed/webhooks`) and from inside a job (`NotifyPartnerWebhookRequest`),
  signed with Standard Webhooks; the partner receiver verifies the signature.
- **Postgres** — provisioned by the AppHost (`AddPostgres("postgres").AddDatabase("TestContext")`); the
  connection string is injected as `ConnectionStrings:TestContext`. No manual DB setup. The container is
  pinned to `127.0.0.1:5442` with the demo credentials `postgres`/`admin`, so you can attach `psql` or a
  GUI client to the running demo database (`psql -h 127.0.0.1 -p 5442 -U postgres -d TestContext`). The
  standalone `appsettings.json` of `Warp.TestApp` / `Warp.TestWorker` — used when you run those projects
  WITHOUT the AppHost — points at that same **server**, but at its own `warp` database, which you create
  yourself as before; the AppHost provisions `TestContext`. 5442 is deliberately clear of the usual
  5432-5434 range and of the ephemeral range Docker draws random published ports from.
