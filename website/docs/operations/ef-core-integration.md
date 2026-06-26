---
sidebar_position: 2
---

# EF Core Integration

Warp lives inside your `DbContext` — there's no separate broker, no second connection pool, and no out-of-process orchestration. That means Warp depends on a few specific things from your EF Core setup. This page documents the contract and the failure modes when that contract isn't met.

## Which call does what

The registration surface splits responsibilities across four calls. Knowing which one contributes the **EF model**, which adds the **worker**, and which wires the **runtime store** is the key to reasoning about migrations and multi-host deployments:

| Call | Contributes |
|---|---|
| `AddWarp<TContext>(opt => …)` | The mediator, publish-side services (`IPublisher`, query services), **and the EF Core model** (wires `WarpModelCustomizer` onto `DbContextOptions<TContext>`). Use it for dashboard-only / publisher-only processes. |
| `AddWarpServer<TContext>(opt => …)` | Everything `AddWarp` does **plus the job worker hosts and server tasks**. Calls `AddWarp` internally. (`opt.DisableWorker()` → service-only server.) |
| `opt.UsePostgreSql()` / `opt.UseSqlServer()` | The **runtime job store and lock provider** for that database — the notification transport, row-lock SQL, `IWarpSqlQueries<TContext>`. It does **not** contribute the EF model; the model comes from `AddWarp`'s customizer regardless of provider. |
| `AddWarpHttp()` / `MapWarpHttp()` | HTTP endpoint generation and mapping (the `[WarpHttpGet/Post/…]` surface). Independent of the model. |

> **2.0 rename:** `AddWarpWorker` / `WarpWorkerConfiguration` / `WarpWorkerBuilder` were renamed to `AddWarpServer` / `WarpServerConfiguration` / `WarpServerBuilder`. The worker is now a component of the server.

The subtlety that bites people: **the model is contributed by `AddWarp`, not by the provider.** A host that calls `UsePostgreSql()` but runs `MigrateAsync` without `AddWarp` (a bare migrator, an Aspire migration host) configures the runtime store but **not** the model — so EF sees a model without the Warp tables and the migration diverges. The next section is how to avoid that.

## DbContext registration — `AddDbContext`, not `AddDbContextFactory` alone

`AddWarp<TContext>` requires `TContext` to be registered as **Scoped**:

```csharp
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connStr));
builder.Services.AddWarp<AppDbContext>(opt => opt.UsePostgreSql());
```

`AddDbContext<T>` registers both `DbContextOptions<T>` (Singleton) and `TContext` itself (Scoped). Warp's scoped services (`IPublisher`, `IMediator`, `IJobCommandService`, etc.) constructor-inject `TContext`, so a Scoped registration is mandatory.

### `AddDbContextFactory<T>` does **not** count

`AddDbContextFactory<T>` registers `IDbContextFactory<T>` but **not** `T` itself. Calling `AddWarp<T>` with only the factory registered throws at startup:

```
System.InvalidOperationException: AddWarp<AppDbContext>() requires AppDbContext to be
registered via services.AddDbContext<AppDbContext>(...). If you're using
AddDbContextFactory<AppDbContext>(...) (e.g. for Blazor / design-time tooling), also call
AddDbContext<AppDbContext>(...) so Warp's scoped services can resolve the context.
```

If your app needs the factory pattern (Blazor Server is the usual case — the factory provides a fresh context per render scope to avoid concurrent enumerator issues), call **both**:

```csharp
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(connStr));
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connStr));
builder.Services.AddWarp<AppDbContext>(opt => opt.UsePostgreSql());
```

### Design-time tooling (`dotnet ef migrations`)

EF Core's design-time tooling bypasses your runtime DI container. It looks for an `IDesignTimeDbContextFactory<T>` or — failing that — boots your `Program.Main` with a minimal host and asks DI for a `TContext`.

Warp's model customizer is registered on `DbContextOptions<TContext>` by `AddWarp`'s service wiring. If design-time tooling can't reach that wiring, it instantiates your `DbContext` without the model customizer and generates an **empty migration** — silently, no warning.

**Symptoms:** `dotnet ef migrations add UpgradeWarp` produces a file with empty `Up`/`Down` methods even though your `AddWarp<T>` call references new entities (new addon opt-ins, etc.).

**Root cause:** Either an `IDesignTimeDbContextFactory<T>` that constructs the `DbContext` directly without going through `Host.CreateApplicationBuilder` + `AddDbContext` + `AddWarp`, or a migrations project that uses `AddDbContextFactory` instead of `AddDbContext`.

**Fix:** Make the migrations host a real `Host.CreateApplicationBuilder` that calls `AddDbContext` **and** `AddWarp<T>` — including any addon opt-ins (`opt.AddConcurrency()`, etc.) that contribute entities. Concretely:

```csharp
// Migrations/Program.cs
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connStr));

builder.Services.AddWarp<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
});

builder.Build().Run();
```

Then `dotnet ef migrations add UpgradeWarp` from this project picks up the model customizer and generates a non-empty migration.

### Explicit model registration with `ApplyWarpModel` (recommended for migrator / multi-host)

The implicit `IModelCustomizer` wiring works, but it lives on `DbContextOptions` — so any host that builds its context options *differently* (a bare migrator, design-time tooling that constructs the context directly) sees a different model than your runtime. The robust alternative is to declare Warp's model in the context's **own** `OnModelCreating`:

```csharp
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // your own entities …

        modelBuilder.ApplyWarpModel(schema: "warp"); // pass null for the DB default schema
    }
}
```

Because the model is now part of the context definition, **every** host — your API, your Workers, a standalone migrator, `dotnet ef` — sees an identical model with no `DbContextOptions` divergence. You still call `AddWarp`/`AddWarpServer` at runtime for the services; `ApplyWarpModel` is **idempotent**, so the implicit customizer detects the model is already present and does nothing. There's no double registration and no conflict.

This is the recommended pattern when you have a dedicated migrations project or an Aspire-style migration host: the migrator only needs `AddDbContext<AppDbContext>` (so `dotnet ef` / `MigrateAsync` can build the model) — the Warp tables come along automatically because they're declared in `OnModelCreating`.

> `ApplyWarpModel` covers all in-tree entities (job store + addon entities) and the UTC converters. External addon entities contributed via `WarpConfiguration.EntityConfigurators` still flow through the DI customizer path, so a host using those must register `AddWarp` for the migration to include them.

### The "model not applied" guard

If you publish against a context that never received the Warp model — e.g. a test or migrator context that registered `UsePostgreSql()` but neither `AddWarp` nor `ApplyWarpModel` — Warp now fails with an explicit message instead of EF Core's cryptic `Cannot create a DbSet for 'Job'`:

```
System.InvalidOperationException: Warp's EF Core model is not present on 'AppDbContext'.
Warp stages jobs on your own DbContext (the transactional outbox), so the context must
include Warp's entities. Fix it one of two ways: (1) call AddWarp<AppDbContext>(...) before
the context's options are built …, or (2) call modelBuilder.ApplyWarpModel(schema) inside
AppDbContext.OnModelCreating …
```

For unit-testing a handler that publishes **without** any Warp store at all, use the in-memory test publisher instead of a real context — see [Testing handlers that publish](#testing-handlers-that-publish).

## Addon entities are always in the schema

Every addon's EF entities (`ConcurrencyLimit`, `CircuitBreakerState`, `RateLimitBucket`, `RateLimitOverride`, `SagaState`, `SagaJobLink`) are registered unconditionally by `WarpModelCustomizer`, regardless of whether the host called `opt.AddConcurrency()` / `AddCircuitBreaker()` / `AddRateLimit()` / `AddSagas()`. This means:

- A single migration covers every Warp deployment shape — no need to mirror opt-ins between your API host, Workers host, and the migrations host.
- An empty schema on day one is identical to the schema after you opt into the rate-limit addon a year later.
- The builder methods (`opt.AddRateLimit()` etc.) still gate the **runtime behavior**: pipeline behaviors, admin services, and dashboard endpoints. Without opting in, the tables exist but the `[Mutex]` / `[RateLimit]` / `[CircuitBreaker]` attributes on your handlers are no-ops.

The tradeoff: 6 unused tables in deployments that don't use these addons. They're cheap (empty B-trees, no inserts, no indexes touched) and the operational simplification — one schema, no opt-in mirroring — is worth it. If you actively need to avoid the tables (multi-tenant DB with very strict schema discipline), file an issue.

## `IModelCustomizer` and naming conventions

`AddWarp` wires a custom `IModelCustomizer` on `DbContextOptions<TContext>`. That customizer:

1. Adds Warp's core entities (`Job`, `JobLog`, `Server`, `Worker`, `ServerTask`, `ServerLog`, `RecurringJob`, `RecurringJobLog`, `BackgroundServiceDefinition`, `BackgroundServiceInstance`, `BackgroundServiceLease`, `BackgroundServiceLog`).
2. Adds every addon entity unconditionally (`ConcurrencyLimit`, `CircuitBreakerState`, `RateLimitBucket`, `RateLimitOverride`, `SagaState`, `SagaJobLink`) — see "Addon entities are always in the schema" above.
3. Replays any callbacks contributed via `WarpConfiguration.EntityConfigurators` (extension point for third-party / provider-package entities).
4. Sets the schema via `entity.Metadata.SetSchema(schema)` — **not** `.ToTable(...)` — so EF naming conventions (e.g., `UseSnakeCaseNamingConvention()`) can transform table and column names without you having to re-pin them.

If you compose your own `IModelCustomizer` chain, the Warp customizer must run **after** your entity registrations (it doesn't depend on yours, but composability is one-directional).

## Testing handlers that publish

A handler that calls `IPublisher.Enqueue` / `Publish` / `Schedule` doesn't need a database to be unit-testable. Warp ships `InMemoryPublisher` (in `Warp.Core.Testing`) — a drop-in `IPublisher` that records every publish in memory and never touches a `DbContext`:

```csharp
using Warp.Core.Testing;

var publisher = new InMemoryPublisher();

// register it in place of the real publisher, or pass it to the handler directly
services.AddSingleton<IPublisher>(publisher);

// … exercise the handler …

var published = publisher.Published.ShouldHaveSingleItem();
published.Kind.ShouldBe(PublishedJobKind.Job);
published.Payload.ShouldBeOfType<SendEmailJob>();
publisher.SaveChangesCount.ShouldBe(1);
```

`Published` records each call's payload, kind (`Job` / `Message`), queue, schedule time, and parent id; `SaveChangesCount` tracks `SaveChangesAsync` calls. `InMemoryPublisher` also implements `IBatchPublisher`, so handlers that fan out batches (`StartNew` / `ContinueBatchWith`) are testable the same way — recorded calls land in `Batches`. This is the supported way to test publish-side logic without standing up the Warp store — no `Job` DbSet, no migrations, no container.

> Other Warp services don't need a fake: `IMediator` dispatches in-memory (no DB), and `IJobContext` is a plain `JobContext` you can `new` up directly. The query/command services (`IJobQueryService`, `IJobCommandService`, etc.) *are* the data-access layer — test those against a real database.

## Server-internal logging — the Warp server context

Warp's server-internal work — the **job worker** (fetch/execute/complete), the `Heartbeat`, `Orchestrator`, `MessageRouter`, `ScheduledJobActivation`, `RecurringJobScheduler`, `StaleJobRecovery`, `CounterAggregator`, the cleanups, and the background-service host — polls the database continuously. If those queries ran on your `DbContext`, EF Core's command logging would flood your application's logs (under `Microsoft.EntityFrameworkCore.Database.Command`) with Warp's polling SQL, indistinguishable from your own queries.

To prevent that, `AddWarpServer` runs **all** of that work — worker fetch/execute included — on a dedicated **Warp server context**: a runtime-only mirror of the Warp model that maps to the exact same tables as your `DbContext` (names pulled from your context's model, so a naming convention like `UseSnakeCaseNamingConvention()` is honoured), but carries its **own quiet logger**: it demotes the command-executed event to `Debug`. So at a default `Information` log level, **Warp's server SQL no longer appears in your logs, and your own `DbContext`'s command logging is completely unaffected.** No configuration required.

The only DB work left on your `DbContext` is the **outbox** (the publisher staging job rows inside your transaction) and your **handler's own code** (plus any addon pipeline state it commits) — i.e. exactly the queries you'd *want* logged.

The server context is internal infrastructure; you never register or reference it. It's a no-op for `AddWarp`-only (dashboard / publisher-only) processes — it exists only where `AddWarpServer` runs, and a provider (`opt.UsePostgreSql()` / `opt.UseSqlServer()`) supplies its connection from your context's options.

To see the server context's SQL (e.g. when debugging Warp itself), opt back in:

```csharp
builder.Services.AddWarpServer<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
    opt.EnableServerCommandLogging = true; // server SQL logs at the normal level
});
```

## Connection string vs `NpgsqlDataSource`

Both work. If your runtime uses `UseNpgsql(dataSource)` (Aspire's `AddAzureNpgsqlDataSource`, Managed Identity, custom SSL/password providers), Warp's notification transport and lock provider thread the same data source through — connections inherit auth and encryption settings. If you pass a connection string instead, Warp uses that for its own connections.

## What lives in the database, what lives in DI

Everything Warp persists is in your `DbContext`. Everything Warp orchestrates (workers, server tasks, background services, dashboard push, distributed locks) is in DI. There is no out-of-band state — no Redis, no message broker, no separate service catalog. A consequence: `EnsureCreatedAsync()` is sufficient for getting started, but migrations are still mandatory for production (so that schema changes ship with your application code, not as a tooling step).
