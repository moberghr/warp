---
sidebar_position: 2
---

# EF Core Integration

Warp lives inside your `DbContext` — there's no separate broker, no second connection pool, and no out-of-process orchestration. That means Warp depends on a few specific things from your EF Core setup. This page documents the contract and the failure modes when that contract isn't met.

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

## Connection string vs `NpgsqlDataSource`

Both work. If your runtime uses `UseNpgsql(dataSource)` (Aspire's `AddAzureNpgsqlDataSource`, Managed Identity, custom SSL/password providers), Warp's notification transport and lock provider thread the same data source through — connections inherit auth and encryption settings. If you pass a connection string instead, Warp uses that for its own connections.

## What lives in the database, what lives in DI

Everything Warp persists is in your `DbContext`. Everything Warp orchestrates (workers, server tasks, background services, dashboard push, distributed locks) is in DI. There is no out-of-band state — no Redis, no message broker, no separate service catalog. A consequence: `EnsureCreatedAsync()` is sufficient for getting started, but migrations are still mandatory for production (so that schema changes ship with your application code, not as a tooling step).
