---
sidebar_position: 1
---

# Demo shop: Warp in an online store

This section walks through every Warp feature in the setting of one ordinary application: an online
store. Each page explains what problem a feature solves in that shop, how it is wired, and a **test
scenario** you can run to see it work. The test scenarios double as acceptance checks: if the scenario
behaves as described, the feature works in your host.

## The shop

**Northwind Goods** sells physical products online. It is a typical .NET shop with a database, a few
deployable services and a handful of third parties.

| Service | Role | Warp setup |
|---|---|---|
| `shop-api` | Storefront and checkout API: catalogue, cart, checkout, order status. ASP.NET controllers plus a few Warp HTTP endpoints. | `AddWarp` (publish only) + dashboard |
| `shop-worker` | Background processing: payments, fulfilment, emails, imports, reports. | `AddWarpServer` (worker + server tasks) |
| `shop-web` | The browser storefront (SPA). | Client observability ingest |
| `migrator` | Applies EF Core migrations before the others start. | — |

All three .NET services set `ApplicationName` (`shop-api`, `shop-worker`) and `ApplicationVersion` from
the build, and share one PostgreSQL (or SQL Server) database.

**Third parties**

| Partner | What the shop does with it |
|---|---|
| **PayFast** (payment provider) | Authorise and capture card payments; receives payment-status webhooks back. |
| **ShipIt** (carrier) | Book shipments, print labels, track parcels. |
| **Stockroom** (warehouse ERP) | Nightly stock import, reservation calls during checkout. |
| **Mailer** (email service) | Order confirmation, shipping and abandoned-cart emails. |
| **Marketplace partners** | Receive the shop's outbound `order.*` webhooks. |

**The order flow** used throughout these pages:

1. A customer checks out on `shop-web`; `shop-api` validates the cart and stores an `Order`.
2. `OrderPlaced` is published. Handlers reserve stock, authorise the payment and send the
   confirmation email.
3. PayFast calls back with the payment result; the order moves to `Paid`.
4. Fulfilment books a ShipIt shipment; the order moves to `Shipped`, then `Delivered` from tracking
   updates.
5. Marketplace partners are notified by webhook at each step.

## How to read the pages

Each feature section follows the same shape:

- **In the shop**: the concrete problem in Northwind Goods.
- **Wiring**: the minimal registration and code, using the real Warp API.
- **Test scenario**: *Given / When / Then* steps with what to look for in the dashboard or database,
  and the automated tests in `src/tests/Warp.Tests` that cover the same behaviour where they exist.

| Page | Features |
|---|---|
| [Orders and checkout](./orders-and-checkout.md) | Messages, jobs, requests and streams, sagas, outbox, metadata, DB-generated ids, cancellation |
| [Reliability](./reliability.md) | Retry, timeout, circuit breaker, rate limit, semaphore, mutex, no-restart, pause, batched completions |
| [Scheduling and background work](./scheduling.md) | Recurring jobs, background services, batches, DB push, dashboard push |
| [Integrations](./integrations.md) | Outbound adapters, durable webhooks, inbound HTTP endpoints |
| [Observability](./observability.md) | Tracing, observability sinks, endpoint observability (including your own controllers), client observability, queue metrics, error grouping (Issues), operational notifications, applications and deploys, post-deploy verdict |
| [Metrics and SLOs](./metrics-and-slos.md) | Custom metrics, metrics retention, SLOs and error budgets, health board, annotations |
