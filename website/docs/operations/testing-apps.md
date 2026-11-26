---
sidebar_position: 7
---

# Testing apps that use Warp

Your application calls `AddWarp` / `AddWarpServer` in `Program.cs`, and its own test suite boots that `Program.cs`, usually through `WebApplicationFactory`. How to keep Warp in place depends on whether a test exercises Warp.

## Tests that don't exercise Warp: `RemoveWarpHostedServices()`

Controller and API tests that mock `IMediator`, or simply never look at jobs, only need the app to start. Out of the box it would not: `AddWarpServer` starts workers, server tasks, server registration and a heartbeat, and the provider's startup checks, all of which talk to the database as soon as the host starts.

Remove them in `ConfigureTestServices`, after the app's own registrations:

```csharp
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Warp.Core.Testing;

public sealed class AppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services => services.RemoveWarpHostedServices());
    }
}
```

`RemoveWarpHostedServices()` (namespace `Warp.Core.Testing`, in `Warp.Core`) removes every `IHostedService` that belongs to a Warp package: the worker and dispatcher hosts, server registration and the heartbeat, the server tasks, the background-service host, the counter and observability flushers, the metric-board and SLO seeders, dashboard push, and the startup checks. Your own hosted services stay. A Warp hosted service is recognised by its assembly, which every Warp package marks, not by a list, so new ones are covered.

What that gives you:

- **The app starts with no database at all.** The app's `DbContext` can stay on PostgreSQL or SQL Server with nothing listening: nothing in Warp connects at startup any more.
- **Or with EF Core in-memory**, if the tests use the context. Swap it in the same `ConfigureTestServices`, removing both registrations EF Core keeps for the provider:

  ```csharp
  services.RemoveAll<DbContextOptions<AppDbContext>>();
  services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>(); // Microsoft.EntityFrameworkCore.Infrastructure
  services.AddDbContext<AppDbContext>(x => x.UseInMemoryDatabase($"tests-{Guid.NewGuid():N}"));
  services.RemoveWarpHostedServices();
  ```

  Without the second `RemoveAll`, EF Core fails with "Services for database providers ... have been registered". Compute the database name once, outside the lambda, if several scopes of one test should see the same data: the lambda runs per scope.
- **Everything else Warp registered stays.** `IMediator` works as in production. Publishing still stages `Job` rows on whatever context the app uses and saves them with it (on in-memory you can read them back with `context.Set<Job>()`), but nothing runs them: no worker, no message routing, no scheduling.

## Tests that need jobs to run: a real database

When the test is about what a job does once it runs (a handler's effect, a message's fan-out, retries, a mutex, a saga, a schedule), run the real server against a real database. Keep the app exactly as it is and point it at a container with [Testcontainers](https://dotnet.testcontainers.org/):

```csharp
using Testcontainers.PostgreSql;

public sealed class AppWithDatabaseFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder().WithImage("postgres:17").Build();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public new async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());
    }
}
```

Apply your migrations when the factory starts (they include Warp's tables) and reset the data between tests (for example with Respawn) rather than recreating the container. Jobs run on Warp's workers, asynchronously: wait for the effect you expect (poll the row, or the job's state through `IJobQueryService`) rather than sleeping.

## Startup checks on a non-relational context

Without `RemoveWarpHostedServices()`, Warp checks a few things when the host starts. The checks that read the database or its column types skip, with a `Debug` log, when your context runs on a provider they do not apply to (EF in-memory, or the other database): the storage-type check on Warp's columns, the PostgreSQL time-column and server-version checks, and the SQL Server compatibility-level check. The check that Warp's model is on your context is provider-agnostic and still runs.

Skipping the checks is not enough to run a server on EF in-memory: `AddWarpServer`'s workers and server tasks use transactions, bulk updates and provider SQL that only a relational database supports. Remove the hosted services, or use a real database.
